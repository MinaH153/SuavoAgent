using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using SuavoAgent.Contracts.Behavioral;

namespace SuavoAgent.Helper.SystemObservers.BrowserConnector;

internal static class BrowserObservationRelayConstants
{
    public const string Protocol = "suavo-browser-relay-v1";
    public const int ProtocolVersion = 1;
    public const int MaximumFrameBytes = 4_096;
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan MinimumLeaseRemaining = TimeSpan.FromSeconds(30);
}

internal readonly record struct BrowserRelayPeerVerification(bool Trusted, string ReasonCode)
{
    public static BrowserRelayPeerVerification Allow() =>
        new(true, BrowserConnectorReasonCodes.Ready);

    public static BrowserRelayPeerVerification Deny(string reasonCode) =>
        new(false, BrowserConnectorReasonCodes.IsSafe(reasonCode)
            ? reasonCode
            : BrowserConnectorReasonCodes.AuthenticationRejected);
}

internal interface IBrowserRelayPeerIdentityVerifier
{
    ValueTask<BrowserRelayPeerVerification> VerifyClientAsync(
        NamedPipeServerStream pipe,
        CancellationToken cancellationToken);

    ValueTask<BrowserRelayPeerVerification> VerifyServerAsync(
        NamedPipeClientStream pipe,
        CancellationToken cancellationToken);
}

internal interface IBrowserRelayDuplex : IAsyncDisposable
{
    Stream Stream { get; }
}

internal interface IBrowserRelayClientTransport
{
    Task<IBrowserRelayDuplex> ConnectAsync(CancellationToken cancellationToken);
}

internal interface IBrowserRelayServerTransport
{
    Task<IBrowserRelayDuplex> AcceptAsync(CancellationToken cancellationToken);
}

internal interface IBrowserRelayEntropy
{
    void Fill(Span<byte> destination);
}

internal sealed class CryptographicBrowserRelayEntropy : IBrowserRelayEntropy
{
    public void Fill(Span<byte> destination) => RandomNumberGenerator.Fill(destination);
}

internal static class BrowserDomainHashKeyDerivation
{
    public static byte[] Derive(
        ReadOnlySpan<byte> observationLeaseKey,
        string leaseId,
        string sessionBinding,
        long epoch)
    {
        if (observationLeaseKey.Length is < 32 or > 64 ||
            !BrowserRelayProtocol.IsOpaqueIdentifier(leaseId) ||
            !BrowserRelayProtocol.IsOpaqueIdentifier(sessionBinding) ||
            epoch <= 0)
            throw new ArgumentException("browser_domain_hash_context_invalid");
        var context = Encoding.UTF8.GetBytes(
            $"browser-domain-hash-v1\0{leaseId}\0{sessionBinding}\0{epoch}");
        try
        {
            return HMACSHA256.HashData(observationLeaseKey, context);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(context);
        }
    }
}

internal sealed class BrowserRelayProtocolException : Exception
{
    public BrowserRelayProtocolException(string reasonCode)
        : base("Browser relay protocol rejected a frame.")
    {
        ReasonCode = BrowserConnectorReasonCodes.IsSafe(reasonCode)
            ? reasonCode
            : BrowserConnectorReasonCodes.InternalFailure;
    }

    public string ReasonCode { get; }
}

internal sealed class BrowserRelayLeaseSnapshot : IDisposable
{
    private int _disposed;

    private BrowserRelayLeaseSnapshot(
        string leaseId,
        string sessionBinding,
        long epoch,
        DateTimeOffset expiresAtUtc,
        byte[] keyMaterial)
    {
        LeaseId = leaseId;
        SessionBinding = sessionBinding;
        Epoch = epoch;
        ExpiresAtUtc = expiresAtUtc;
        KeyMaterial = keyMaterial;
    }

    public string LeaseId { get; }

    public string SessionBinding { get; }

    public long Epoch { get; }

    public DateTimeOffset ExpiresAtUtc { get; }

    public byte[] KeyMaterial { get; }

    public static BrowserRelayLeaseSnapshot? TryCreate(
        ObservationKeyLease? lease,
        DateTimeOffset now)
    {
        if (lease is null ||
            lease.ContractVersion != ObservationKeyLease.CurrentContractVersion ||
            !BrowserRelayProtocol.IsOpaqueIdentifier(lease.LeaseId) ||
            !BrowserRelayProtocol.IsOpaqueIdentifier(lease.SessionBinding) ||
            lease.Epoch <= 0 ||
            lease.ExpiresAtUtc <= now + BrowserObservationRelayConstants.MinimumLeaseRemaining ||
            !TryDecodeKey(lease.KeyMaterial, out var key))
        {
            return null;
        }

        return new BrowserRelayLeaseSnapshot(
            lease.LeaseId,
            lease.SessionBinding,
            lease.Epoch,
            lease.ExpiresAtUtc,
            key);
    }

    public bool Matches(ObservationKeyLease? lease, DateTimeOffset now)
    {
        if (lease is null ||
            lease.ContractVersion != ObservationKeyLease.CurrentContractVersion ||
            lease.ExpiresAtUtc <= now ||
            lease.Epoch != Epoch ||
            !string.Equals(lease.LeaseId, LeaseId, StringComparison.Ordinal) ||
            !string.Equals(lease.SessionBinding, SessionBinding, StringComparison.Ordinal) ||
            !TryDecodeKey(lease.KeyMaterial, out var candidate))
        {
            return false;
        }

        try
        {
            return candidate.Length == KeyMaterial.Length &&
                   CryptographicOperations.FixedTimeEquals(candidate, KeyMaterial);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(candidate);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        CryptographicOperations.ZeroMemory(KeyMaterial);
    }

    private static bool TryDecodeKey(string? encoded, out byte[] key)
    {
        key = Array.Empty<byte>();
        if (string.IsNullOrWhiteSpace(encoded) || encoded.Length > 128)
            return false;
        try
        {
            key = Convert.FromBase64String(encoded);
            if (key.Length is >= 32 and <= 64)
                return true;
            CryptographicOperations.ZeroMemory(key);
            key = Array.Empty<byte>();
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
