using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.State;

namespace SuavoAgent.Core.Workers;

public sealed partial class HeartbeatWorker
{
    private sealed record ReleaseNoopConvergenceBinding(
        string ReleaseTag,
        string SourceSha,
        string ManifestName,
        string ChecksumsSha256,
        string ChecksumsSignatureSha256,
        string InventorySha256,
        string InstallReceiptSha256,
        string RestartReceiptSha256);

    private void HandleSameVersionUpdate(
        JsonElement data,
        UpdateManifest manifest,
        string manifestCanonical,
        string? manifestSignature,
        string commandId,
        SignedCommand command,
        string receiptState,
        string? targetChannel)
    {
        var manifestValidation = UpdateActivationContract.ValidateManifest(
            manifestCanonical,
            manifestSignature ?? string.Empty,
            OtaUpdateTrust.ProductionTrustedPublicKeys);
        if (!manifestValidation.IsValid)
        {
            _logger.LogWarning(
                "Same-version OTA manifest rejected before confirmation: {Code}",
                manifestValidation.Code);
            WriteUpdateHealthEvidence(
                "failed",
                manifest.Version,
                manifestValidation.Code,
                consecutiveFailures: 1,
                channel: targetChannel);
            return;
        }

        var otaSigningKeyId = SelfUpdater.ResolveManifestSigningKeyId(
            manifestCanonical,
            manifestSignature,
            _logger);
        if (otaSigningKeyId is null)
        {
            WriteUpdateHealthEvidence(
                "failed",
                manifest.Version,
                "manifest_signing_root_unresolved",
                consecutiveFailures: 1,
                channel: targetChannel);
            return;
        }

        if (!TryReadReleaseNoopConvergenceBinding(data, out var convergence))
        {
            _logger.LogWarning(
                "Same-version OTA convergence binding is partial or malformed — rejecting");
            WriteUpdateHealthEvidence(
                "failed",
                manifest.Version,
                "convergence_binding_invalid",
                consecutiveFailures: 1,
                channel: targetChannel);
            return;
        }

        var signer = _serviceProvider.GetService<IDeviceAuthoritySigner>()
            ?? throw new InvalidOperationException(
                "Device authority signer is unavailable for release no-op proof.");
        var noOpReceipt = new ReleaseNoopDeviceReceipt(
            SchemaVersion: 1,
            Purpose: AgentStateDb.ReleaseNoopPurpose,
            CommandId: commandId,
            Command: command.Command,
            AgentId: command.AgentId,
            MachineFingerprint: command.MachineFingerprint,
            CommandTimestamp: command.Timestamp,
            EnvelopeNonce: command.Nonce,
            CommandDataHash: command.DataHash,
            CommandKeyId: command.KeyId,
            CommandSignature: command.Signature,
            TargetVersion: manifest.Version,
            ManifestCanonical: manifestCanonical,
            ManifestSignature: manifestSignature!,
            OtaSigningKeyId: otaSigningKeyId,
            ReleaseTag: convergence?.ReleaseTag,
            SourceSha: convergence?.SourceSha,
            ManifestName: convergence?.ManifestName,
            ChecksumsSha256: convergence?.ChecksumsSha256,
            ChecksumsSignatureSha256: convergence?.ChecksumsSignatureSha256,
            InventorySha256: convergence?.InventorySha256,
            InstallReceiptSha256: convergence?.InstallReceiptSha256,
            RestartReceiptSha256: convergence?.RestartReceiptSha256,
            VerifiedAtUtc: DateTimeOffset.UtcNow.ToString("O"));
        _stateDb.GetOrCreateReleaseNoopDeviceReceipt(noOpReceipt, signer);

        _logger.LogDebug("Already running v{Version} — skipping update", manifest.Version);
        if (receiptState != "confirmed")
            _stateDb.MarkUpdateCommandReceipt(commandId, "confirmed");
        WriteUpdateHealthEvidence(
            "current",
            manifest.Version,
            lastErrorKind: null,
            consecutiveFailures: 0,
            channel: targetChannel);
    }

    private static bool TryReadReleaseNoopConvergenceBinding(
        JsonElement data,
        out ReleaseNoopConvergenceBinding? binding)
    {
        static (bool Present, string? Value) Read(JsonElement source, string name)
        {
            if (!source.TryGetProperty(name, out var value)) return (false, null);
            return value.ValueKind == JsonValueKind.String &&
                   !string.IsNullOrWhiteSpace(value.GetString())
                ? (true, value.GetString())
                : (true, null);
        }

        var releaseTag = Read(data, "releaseTag");
        var sourceSha = Read(data, "sourceSha");
        var manifestName = Read(data, "manifestName");
        var checksumsSha256 = Read(data, "checksumsSha256");
        var checksumsSignatureSha256 = Read(data, "checksumsSignatureSha256");
        var inventorySha256 = Read(data, "inventorySha256");
        var installReceiptSha256 = Read(data, "installReceiptSha256");
        var restartReceiptSha256 = Read(data, "restartReceiptSha256");
        var fields = new[]
        {
            releaseTag,
            sourceSha,
            manifestName,
            checksumsSha256,
            checksumsSignatureSha256,
            inventorySha256,
            installReceiptSha256,
            restartReceiptSha256,
        };
        if (fields.All(field => !field.Present))
        {
            binding = null;
            return true;
        }
        if (fields.Any(field => !field.Present || field.Value is null))
        {
            binding = null;
            return false;
        }

        binding = new(
            releaseTag.Value!,
            sourceSha.Value!,
            manifestName.Value!,
            checksumsSha256.Value!,
            checksumsSignatureSha256.Value!,
            inventorySha256.Value!,
            installReceiptSha256.Value!,
            restartReceiptSha256.Value!);
        return true;
    }
}
