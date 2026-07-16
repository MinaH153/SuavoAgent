using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using SuavoAgent.Diagnostics.Maintenance;
using SuavoAgent.Helper.Actuation;

namespace SuavoAgent.Helper.SystemObservers.BrowserConnector;

internal static class BrowserRelayPipeName
{
    public static string ForCurrentProcess()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Browser relay is Windows-only.");
        var sid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("browser_relay_user_unavailable");
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
            throw new InvalidOperationException("browser_relay_executable_unavailable");
        var exactPath = SandboxProcessTrustVerifier.CanonicalizeExistingFile(executable)
            ?? throw new InvalidOperationException("browser_relay_executable_untrusted");
        var session = Process.GetCurrentProcess().SessionId;
        return $"SuavoAgent-BrowserRelay-v1-{session}-{Hash(exactPath.ToUpperInvariant())}-{Hash(sid)}";
    }

    private static string Hash(string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        try
        {
            return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()[..16];
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}

internal sealed class NamedPipeBrowserRelayServerTransport : IBrowserRelayServerTransport
{
    private readonly string _pipeName;
    private readonly IBrowserRelayPeerIdentityVerifier _identityVerifier;

    public NamedPipeBrowserRelayServerTransport(
        string pipeName,
        IBrowserRelayPeerIdentityVerifier identityVerifier)
    {
        _pipeName = string.IsNullOrWhiteSpace(pipeName)
            ? throw new ArgumentException("A browser relay pipe name is required.", nameof(pipeName))
            : pipeName;
        _identityVerifier = identityVerifier ?? throw new ArgumentNullException(nameof(identityVerifier));
    }

    public async Task<IBrowserRelayDuplex> AcceptAsync(CancellationToken cancellationToken)
    {
        var pipe = CreateServer(_pipeName);
        try
        {
            await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            var verification = await _identityVerifier.VerifyClientAsync(pipe, cancellationToken)
                .ConfigureAwait(false);
            if (!verification.Trusted)
                throw new BrowserRelayProtocolException(verification.ReasonCode);
            return new NamedPipeBrowserRelayDuplex(pipe);
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static NamedPipeServerStream CreateServer(string pipeName)
    {
        const PipeOptions options =
            PipeOptions.Asynchronous |
            PipeOptions.WriteThrough |
            PipeOptions.FirstPipeInstance;
        if (!OperatingSystem.IsWindows())
        {
            return new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                options);
        }

        var user = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("browser_relay_user_unavailable");
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(user);
        security.AddAccessRule(new PipeAccessRule(
            user,
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            options,
            0,
            0,
            security,
            HandleInheritability.None);
    }
}

internal sealed class NamedPipeBrowserRelayClientTransport : IBrowserRelayClientTransport
{
    private readonly string _pipeName;
    private readonly IBrowserRelayPeerIdentityVerifier _identityVerifier;
    private readonly TimeSpan _connectTimeout;

    public NamedPipeBrowserRelayClientTransport(
        string pipeName,
        IBrowserRelayPeerIdentityVerifier identityVerifier,
        TimeSpan? connectTimeout = null)
    {
        _pipeName = string.IsNullOrWhiteSpace(pipeName)
            ? throw new ArgumentException("A browser relay pipe name is required.", nameof(pipeName))
            : pipeName;
        _identityVerifier = identityVerifier ?? throw new ArgumentNullException(nameof(identityVerifier));
        _connectTimeout = connectTimeout ?? BrowserObservationRelayConstants.ConnectTimeout;
        if (_connectTimeout <= TimeSpan.Zero || _connectTimeout > TimeSpan.FromSeconds(10))
            throw new ArgumentOutOfRangeException(nameof(connectTimeout));
    }

    public async Task<IBrowserRelayDuplex> ConnectAsync(CancellationToken cancellationToken)
    {
        var pipe = new NamedPipeClientStream(
            ".",
            _pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough,
            TokenImpersonationLevel.Identification);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_connectTimeout);
        try
        {
            await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
            var verification = await _identityVerifier.VerifyServerAsync(pipe, deadline.Token)
                .ConfigureAwait(false);
            if (!verification.Trusted)
                throw new BrowserRelayProtocolException(verification.ReasonCode);
            return new NamedPipeBrowserRelayDuplex(pipe);
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}

internal sealed class NamedPipeBrowserRelayDuplex : IBrowserRelayDuplex
{
    private readonly PipeStream _pipe;

    public NamedPipeBrowserRelayDuplex(PipeStream pipe) =>
        _pipe = pipe ?? throw new ArgumentNullException(nameof(pipe));

    public Stream Stream => _pipe;

    public async ValueTask DisposeAsync() => await _pipe.DisposeAsync().ConfigureAwait(false);
}

/// <summary>
/// Pins each connected pipe endpoint to the same user, Windows session, exact
/// installed Helper path, and exact allowlisted MKM Authenticode signer.
/// Pipe PIDs come from the connected kernel object; caller-supplied PIDs are
/// never trusted.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsBrowserRelayPeerIdentityVerifier : IBrowserRelayPeerIdentityVerifier
{
    private readonly string? _expectedPath;
    private readonly WindowsProcessIdentityEvidence _expectedIdentity;
    private readonly bool _identityAvailable;
    private readonly bool _selfTrusted;

    public WindowsBrowserRelayPeerIdentityVerifier()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var processPath = Environment.ProcessPath;
        _expectedPath = string.IsNullOrWhiteSpace(processPath)
            ? null
            : SandboxProcessTrustVerifier.CanonicalizeExistingFile(processPath);
        _identityAvailable = WindowsProcessIdentityVerifier.TryGetCurrent(
            out _expectedIdentity);
        _selfTrusted = _expectedPath is not null &&
            AuthenticodePublisherVerifier.Verify(_expectedPath).IsTrusted;
    }

    public ValueTask<BrowserRelayPeerVerification> VerifyClientAsync(
        NamedPipeServerStream pipe,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows() ||
            !GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var processId))
            return ValueTask.FromResult(Deny());
        return ValueTask.FromResult(VerifyProcess(processId));
    }

    public ValueTask<BrowserRelayPeerVerification> VerifyServerAsync(
        NamedPipeClientStream pipe,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows() ||
            !GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var processId))
            return ValueTask.FromResult(Deny());
        return ValueTask.FromResult(VerifyProcess(processId));
    }

    private BrowserRelayPeerVerification VerifyProcess(uint processId)
    {
        if (!_selfTrusted ||
            !_identityAvailable ||
            processId == 0 ||
            _expectedPath is null ||
            !WindowsProcessIdentityVerifier.Matches(processId, _expectedIdentity))
            return Deny();

        var rawPath = ProcessImageInterop.Get(processId, out _);
        if (string.IsNullOrWhiteSpace(rawPath))
            return Deny();
        var exactPath = SandboxProcessTrustVerifier.CanonicalizeExistingFile(rawPath);
        if (exactPath is null ||
            !string.Equals(exactPath, _expectedPath, StringComparison.OrdinalIgnoreCase) ||
            !AuthenticodePublisherVerifier.Verify(exactPath).IsTrusted)
            return Deny();
        return BrowserRelayPeerVerification.Allow();
    }

    private static BrowserRelayPeerVerification Deny() =>
        BrowserRelayPeerVerification.Deny(BrowserConnectorReasonCodes.AuthenticationRejected);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(
        Microsoft.Win32.SafeHandles.SafePipeHandle pipe,
        out uint clientProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(
        Microsoft.Win32.SafeHandles.SafePipeHandle pipe,
        out uint serverProcessId);

}
