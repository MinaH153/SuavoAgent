using System.Security.Cryptography;
using System.Text.Json;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Contracts.Security;

namespace SuavoAgent.Setup.Maintenance;

internal enum PioneerRxHighWaterDecisionKind
{
    Advance,
    ExactReplay,
    Rollback,
    Conflict,
}

internal sealed record PioneerRxHighWaterDecision(
    PioneerRxHighWaterDecisionKind Kind,
    string Code,
    PioneerRxApprovalHighWaterState Proposed);

/// <summary>
/// SYSTEM-owned monotonic authority ledger. Counter is primary; within one counter, the signed
/// authority issue time is monotonic so a same-counter revocation or renewal can land while the
/// older still-unexpired authority can never be replayed afterward.
/// </summary>
internal sealed class PioneerRxApprovalHighWaterLedger
{
    private readonly string _path;
    private readonly Action<string> _protect;
    private readonly Func<string, bool> _validate;
    private readonly object _gate = new();

    internal PioneerRxApprovalHighWaterLedger(
        string path,
        Action<string>? protect = null,
        Func<string, bool>? validate = null)
    {
        _path = Path.GetFullPath(path ?? throw new ArgumentNullException(nameof(path)));
        _protect = protect ?? DefaultProtect;
        _validate = validate ?? DefaultValidate;
    }

    internal PioneerRxApprovalHighWaterState? Read()
    {
        lock (_gate)
        {
            if (!File.Exists(_path)) return null;
            if (!_validate(_path))
                throw new UnauthorizedAccessException("PioneerRx high-water ACL is invalid.");
            var bytes = BoundedFile.ReadBytes(
                _path,
                PioneerRxApprovalMaintenanceContract.MaximumJsonBytes);
            try
            {
                if (!PioneerRxApprovalMaintenanceContract.TryDeserializeHighWater(
                        bytes,
                        out var state))
                    throw new InvalidDataException("PioneerRx high-water content is invalid.");
                return state;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
    }

    internal PioneerRxHighWaterDecision Evaluate(
        PioneerRxApprovalInstallRequest request,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(request);
        var proposed = BuildProposed(request, now);
        var existing = Read();
        if (existing is null)
            return new(PioneerRxHighWaterDecisionKind.Advance, "advance", proposed);
        if (proposed.ApprovalCounter < existing.ApprovalCounter)
            return new(PioneerRxHighWaterDecisionKind.Rollback, "approval_counter_rollback", proposed);
        if (proposed.ApprovalCounter > existing.ApprovalCounter)
            return new(PioneerRxHighWaterDecisionKind.Advance, "advance", proposed);

        if (!string.Equals(existing.ReceiptId, proposed.ReceiptId, StringComparison.Ordinal) ||
            !string.Equals(
                existing.VendorCatalogId,
                proposed.VendorCatalogId,
                StringComparison.Ordinal))
            return new(PioneerRxHighWaterDecisionKind.Conflict, "approval_generation_conflict", proposed);

        if (ExactAuthority(existing, proposed))
        {
            // Command ids are delivery/ack identities, not authority generations. The same
            // signed artifacts may arrive first through bootstrap and later through Heartbeat
            // under a different command id. Accept that exact artifact replay and bind the
            // protected projection/completion to the newest command without weakening the
            // authority issue-time high-water mark.
            return new(PioneerRxHighWaterDecisionKind.ExactReplay, "exact_replay", proposed);
        }

        var existingIssued = ParseUtc(existing.AuthorityIssuedAtUtc);
        var proposedIssued = ParseUtc(proposed.AuthorityIssuedAtUtc);
        if (proposedIssued < existingIssued)
            return new(PioneerRxHighWaterDecisionKind.Rollback, "approval_authority_rollback", proposed);
        if (proposedIssued == existingIssued)
            return new(PioneerRxHighWaterDecisionKind.Conflict, "approval_authority_conflict", proposed);
        return new(PioneerRxHighWaterDecisionKind.Advance, "advance", proposed);
    }

    internal void Commit(PioneerRxApprovalHighWaterState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (_gate)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(
                state,
                PioneerRxApprovalMaintenanceContract.JsonOptions);
            try
            {
                if (!PioneerRxApprovalMaintenanceContract.TryDeserializeHighWater(bytes, out _))
                    throw new InvalidDataException("Refusing to persist invalid PioneerRx high-water state.");
                WriteAtomic(bytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
    }

    internal static PioneerRxApprovalHighWaterState BuildProposed(
        PioneerRxApprovalInstallRequest request,
        DateTimeOffset now) => new(
        PioneerRxApprovalMaintenanceContract.SchemaVersion,
        request.ProtocolEpoch,
        request.Receipt.ApprovalCounter,
        request.Receipt.ReceiptId,
        request.CommandId,
        request.PayloadDigest,
        request.VendorCatalog.CatalogId,
        request.Authority.IssuedAtUtc,
        PioneerRxApprovalMaintenanceContract.ComputeAuthorityDigest(request.Authority),
        PioneerRxProcessApprovalContract.IsReceiptRevoked(
            request.Authority,
            request.Receipt.ReceiptId),
        now.UtcDateTime.ToString(
            PioneerRxProcessApprovalContract.UtcTimestampFormat,
            System.Globalization.CultureInfo.InvariantCulture));

    private void WriteAtomic(ReadOnlySpan<byte> bytes)
    {
        var directory = Path.GetDirectoryName(_path)
                        ?? throw new InvalidDataException("High-water parent is missing.");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(
            directory,
            $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            _protect(temporary);
            if (!_validate(temporary))
                throw new UnauthorizedAccessException("High-water temporary ACL is invalid.");
            File.Move(temporary, _path, overwrite: true);
            _protect(_path);
            if (!_validate(_path))
                throw new UnauthorizedAccessException("High-water installed ACL is invalid.");
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    private static bool ExactAuthority(
        PioneerRxApprovalHighWaterState left,
        PioneerRxApprovalHighWaterState right) =>
        left.ApprovalCounter == right.ApprovalCounter &&
        string.Equals(left.ReceiptId, right.ReceiptId, StringComparison.Ordinal) &&
        string.Equals(left.VendorCatalogId, right.VendorCatalogId, StringComparison.Ordinal) &&
        string.Equals(left.AuthorityIssuedAtUtc, right.AuthorityIssuedAtUtc, StringComparison.Ordinal) &&
        FixedHexEquals(left.AuthorityDigest, right.AuthorityDigest) &&
        left.Revoked == right.Revoked;

    private static DateTimeOffset ParseUtc(string value) =>
        DateTimeOffset.ParseExact(
            value,
            PioneerRxProcessApprovalContract.UtcTimestampFormat,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal |
            System.Globalization.DateTimeStyles.AdjustToUniversal);

    private static bool FixedHexEquals(string left, string right)
    {
        var leftBytes = Convert.FromHexString(left);
        var rightBytes = Convert.FromHexString(right);
        try { return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes); }
        finally
        {
            CryptographicOperations.ZeroMemory(leftBytes);
            CryptographicOperations.ZeroMemory(rightBytes);
        }
    }

    private static void DefaultProtect(string path)
    {
        if (OperatingSystem.IsWindows())
            PioneerRxApprovalMetadataAcl.ProtectHighWaterFile(path);
    }

    private static bool DefaultValidate(string path) =>
        !OperatingSystem.IsWindows() ||
        PioneerRxApprovalMetadataAcl.ValidateFile(path, interactiveRead: false);
}
