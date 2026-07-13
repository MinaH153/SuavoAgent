using System.Text.Json;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.State;

namespace SuavoAgent.Core.Workers;

internal sealed record SelfUninstallPreparationResult(
    bool IsReady,
    string Code,
    string? ArchiveId = null)
{
    public static SelfUninstallPreparationResult Ready(string archiveId) =>
        new(true, "request_queued", archiveId);

    public static SelfUninstallPreparationResult Blocked(string code) =>
        new(false, code);
}

/// <summary>
/// Evidence-first preparation of the authenticated Core-to-Broker uninstall handoff.
/// This class deliberately has no logger: neither raw command data nor archive content
/// can accidentally cross into a non-HIPAA log sink.
/// </summary>
internal static class SelfUninstallCoordinator
{
    // Broker polls at five-second intervals. Three intervals cover scheduler
    // jitter and a restart without ever publishing an authority that Broker is
    // statistically guaranteed to see only after expiration.
    internal static readonly TimeSpan MinimumBrokerHandoffRunway =
        TimeSpan.FromSeconds(15);

    public static async Task<SelfUninstallPreparationResult> PrepareAsync(
        AgentStateDb stateDb,
        AgentOptions options,
        SignedCommand command,
        string dataJson,
        string commandId,
        string requestPath,
        Func<string, string, CancellationToken, Task<SelfUninstallArchiveReceipt?>> uploadArchive,
        IReadOnlyDictionary<string, string> trustedPublicKeys,
        Func<DateTimeOffset> utcNow,
        CancellationToken cancellationToken,
        Func<DateTimeOffset>? authorityUtcNow = null)
    {
        var authorityClock = authorityUtcNow ?? utcNow;
        var published = TryResumePublishedRequest(
            requestPath,
            command,
            dataJson,
            commandId,
            options.MaintenanceAttestationKeyId ?? string.Empty,
            trustedPublicKeys,
            utcNow());
        if (published is not null) return published;

        var initialAuthority = SignedCommandVerifier.VerifyExecutionAuthorityAt(
            command, authorityClock());
        if (!initialAuthority.IsValid)
            return SelfUninstallPreparationResult.Blocked(
                SelfUninstallExpiryCode(initialAuthority));

        var commandValidation = SelfUninstallContract.ValidateSignedCommand(
            command.Command,
            command.AgentId,
            command.MachineFingerprint,
            command.Timestamp,
            command.Nonce,
            command.KeyId,
            command.Signature,
            dataJson,
            command.DataHash,
            commandId,
            options.AgentId ?? string.Empty,
            options.MachineFingerprint ?? string.Empty,
            trustedPublicKeys,
            utcNow());
        if (!commandValidation.IsValid)
            return SelfUninstallPreparationResult.Blocked(commandValidation.Code);

        try
        {
            // This event MUST be part of the archive. Append first; only then export and hash.
            stateDb.AppendChainedAuditEntry(new AuditEntry(
                TaskId: command.AgentId,
                EventType: "self_uninstall",
                FromState: "Active",
                ToState: "SelfUninstallRequested",
                Trigger: "signed_remote_self_uninstall",
                CommandId: commandId,
                RequesterId: "operator",
                Actor: "operator",
                SourceComponent: "heartbeat_worker",
                CaptureReason: "signed_remote_self_uninstall"));

            if (!stateDb.VerifyAuditChain())
                return SelfUninstallPreparationResult.Blocked("audit_chain_invalid");

            using var auditDocument = JsonDocument.Parse(stateDb.ExportAuditArchiveJson());
            using var writebackDocument = JsonDocument.Parse(stateDb.ExportWritebackStatesJson());
            var archivePayload = JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                agentId = command.AgentId,
                pharmacyId = options.PharmacyId ?? string.Empty,
                machineFingerprint = command.MachineFingerprint,
                agentVersion = options.Version,
                commandId,
                commandNonce = command.Nonce,
                archivedAtUtc = utcNow().ToString("O"),
                auditChainValid = true,
                auditEntries = auditDocument.RootElement.Clone(),
                writebackStates = writebackDocument.RootElement.Clone(),
            });
            var archiveDigest = RemoteCommandTrust.ComputeSha256Hex(archivePayload);

            SelfUninstallArchiveReceipt? receipt;
            try
            {
                receipt = await uploadArchive(
                    archivePayload,
                    archiveDigest,
                    cancellationToken);
            }
            catch when (!cancellationToken.IsCancellationRequested)
            {
                return SelfUninstallPreparationResult.Blocked("archive_upload_failed");
            }

            if (receipt is null)
                return SelfUninstallPreparationResult.Blocked("archive_ack_missing");
            if (!string.Equals(
                    receipt.ArchiveDigest,
                    archiveDigest,
                    StringComparison.OrdinalIgnoreCase))
                return SelfUninstallPreparationResult.Blocked("archive_ack_digest_mismatch");

            var postUploadAuthority =
                SignedCommandVerifier.VerifyExecutionAuthorityAt(
                    command, authorityClock(), MinimumBrokerHandoffRunway);
            if (!postUploadAuthority.IsValid)
                return SelfUninstallPreparationResult.Blocked(
                    SelfUninstallExpiryCode(postUploadAuthority));

            var requestedAt = utcNow();
            var request = new SelfUninstallRequest(
                SelfUninstallContract.SchemaVersion,
                command.Command,
                command.AgentId,
                command.MachineFingerprint,
                command.Timestamp,
                command.Nonce,
                command.KeyId,
                command.Signature,
                dataJson,
                command.DataHash,
                commandId,
                requestedAt.ToString("O"),
                archiveDigest,
                receipt);

            // Validate the exact bytes/receipt we are about to persist using the same
            // verifier Broker will run. Missing/unsigned cloud receipt fields fail here.
            var validation = SelfUninstallContract.Validate(
                request,
                command.AgentId,
                command.MachineFingerprint,
                trustedPublicKeys,
                requestedAt);
            if (!validation.IsValid)
                return SelfUninstallPreparationResult.Blocked(validation.Code);

            try
            {
                var written = await WriteRequestAtomicallyAsync(
                    requestPath,
                    request,
                    cancellationToken,
                    () => SignedCommandVerifier.VerifyExecutionAuthorityAt(
                        command,
                        authorityClock(),
                        MinimumBrokerHandoffRunway).IsValid);
                if (!written)
                    return SelfUninstallPreparationResult.Blocked(
                        "self_uninstall_authority_expired");
            }
            catch when (!cancellationToken.IsCancellationRequested)
            {
                return SelfUninstallPreparationResult.Blocked("request_write_failed");
            }

            // Publication is only Core's offer. The nonce remains reusable until
            // Broker durably signs acceptance while the authority is current.
            return SelfUninstallPreparationResult.Blocked(
                "broker_acceptance_pending");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return SelfUninstallPreparationResult.Blocked("self_uninstall_preparation_failed");
        }
    }

    private static string SelfUninstallExpiryCode(VerificationResult result) =>
        string.Equals(
            result.Reason,
            "Live command authority expired",
            StringComparison.Ordinal)
            ? "self_uninstall_authority_expired"
            : string.Equals(
                result.Reason,
                "Live command authority handoff runway insufficient",
                StringComparison.Ordinal)
                ? "self_uninstall_handoff_runway_insufficient"
                : "self_uninstall_expiry_invalid";

    private static SelfUninstallPreparationResult? TryResumePublishedRequest(
        string requestPath,
        SignedCommand command,
        string dataJson,
        string commandId,
        string expectedMaintenanceKeyId,
        IReadOnlyDictionary<string, string> trustedPublicKeys,
        DateTimeOffset now)
    {
        var claimedPath = requestPath + ".claimed";
        var publishedPath = File.Exists(claimedPath)
            ? claimedPath
            : File.Exists(requestPath)
                ? requestPath
                : null;
        if (publishedPath is null) return null;
        try
        {
            var info = new FileInfo(publishedPath);
            if (info.Length is <= 0 or > SelfUninstallContract.MaxRequestBytes)
                return SelfUninstallPreparationResult.Blocked("published_request_conflict");
            var json = File.ReadAllText(publishedPath);
            if (!SelfUninstallContract.TryDeserialize(
                    json, out var request, out _) || request is null)
                return SelfUninstallPreparationResult.Blocked("published_request_conflict");
            if (!string.Equals(request.Command, command.Command, StringComparison.Ordinal) ||
                !string.Equals(request.AgentId, command.AgentId, StringComparison.Ordinal) ||
                !string.Equals(
                    request.MachineFingerprint,
                    command.MachineFingerprint,
                    StringComparison.Ordinal) ||
                !string.Equals(request.Nonce, command.Nonce, StringComparison.Ordinal) ||
                !string.Equals(request.DataJson, dataJson, StringComparison.Ordinal) ||
                !string.Equals(request.DataHash, command.DataHash, StringComparison.Ordinal) ||
                !string.Equals(request.CommandId, commandId, StringComparison.Ordinal))
                return SelfUninstallPreparationResult.Blocked("published_request_conflict");
            var acceptancePath = SelfUninstallAcceptanceContract.PathForClaim(
                publishedPath);
            if (!File.Exists(acceptancePath))
                return SelfUninstallPreparationResult.Blocked(
                    "broker_acceptance_pending");
            var acceptanceJson = File.ReadAllText(acceptancePath);
            if (!SelfUninstallAcceptanceContract.TryDeserialize(
                    acceptanceJson, out var acceptance) || acceptance is null)
                return SelfUninstallPreparationResult.Blocked(
                    "broker_acceptance_invalid");
            var validation = SelfUninstallAcceptanceContract.Validate(
                acceptance,
                request,
                json,
                command.AgentId,
                command.MachineFingerprint,
                expectedMaintenanceKeyId,
                trustedPublicKeys);
            return validation.IsValid
                ? SelfUninstallPreparationResult.Ready(request.ArchiveReceipt.ArchiveId)
                : SelfUninstallPreparationResult.Blocked(validation.Code);
        }
        catch
        {
            return SelfUninstallPreparationResult.Blocked("published_request_conflict");
        }
    }

    internal static async Task<bool> WriteRequestAtomicallyAsync(
        string requestPath,
        SelfUninstallRequest request,
        CancellationToken cancellationToken,
        Func<bool>? authorityCurrent = null)
    {
        var directory = Path.GetDirectoryName(requestPath);
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException("Self-uninstall request directory is missing.");

        Directory.CreateDirectory(directory);
        var tempPath = requestPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            var requestBytes = System.Text.Encoding.UTF8.GetBytes(
                SelfUninstallContract.Serialize(request));
            await using (var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(requestBytes, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            if (authorityCurrent is not null && !authorityCurrent())
                return false;
            File.Move(tempPath, requestPath, overwrite: true);
            return true;
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }
}
