using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Health;
using SuavoAgent.Core.State;

namespace SuavoAgent.Core.Workers;

internal static class PioneerRxShadowFixtureCommand
{
    internal const string RequiredTrack2SourceCommitMinimum =
        "b8f8888279c8960280823c2308a0339fff4a2c74";

    public static async Task HandleAsync(
        JsonElement signedCommandElement,
        SignedCommand command,
        AgentOptions options,
        IServiceProvider serviceProvider,
        AgentStateDb stateDb,
        SuavoCloudClient? cloudClient,
        ILogger logger,
        CancellationToken ct)
    {
        var dataEl = signedCommandElement.TryGetProperty("data", out var d) ? d : signedCommandElement;
        var commandId = dataEl.TryGetProperty("commandId", out var cid) ? cid.GetString() : null;
        var requesterId = dataEl.TryGetProperty("requesterId", out var rid) ? rid.GetString() : "operator";

        async Task AckAsync(bool ok, object? result, string? err)
        {
            if (string.IsNullOrEmpty(commandId) || cloudClient == null) return;
            await cloudClient.AckCommandAsync(commandId, ok, result, err, ct);
        }

        if (ContainsUnsafeField(dataEl))
        {
            logger.LogWarning("export_pioneerrx_shadow_fixture: rejected unsafe payload");
            await AckAsync(false, null, "shadow fixture export payload must be bounded and non-PHI");
            return;
        }

        var maxRows = dataEl.TryGetProperty("maxRows", out var maxRowsEl) && maxRowsEl.TryGetInt32(out var requestedRows)
            ? Math.Clamp(requestedRows, 0, 25)
            : 5;
        var includeSyntheticPatientDetails =
            dataEl.TryGetProperty("includeSyntheticPatientDetails", out var synthEl) &&
            synthEl.ValueKind == JsonValueKind.True;

        stateDb.AppendChainedAuditEntry(new AuditEntry(
            TaskId: commandId ?? command.Nonce,
            EventType: "pioneerrx_shadow_fixture_export_command",
            FromState: "requested",
            ToState: "accepted",
            Trigger: "signed_command",
            CommandId: command.Nonce,
            RequesterId: requesterId,
            Actor: "operator",
            SourceComponent: "heartbeat_worker",
            CaptureReason: "non_phi_pioneerrx_shadow_fixture"));

        var rxWorker = serviceProvider.GetService<RxDetectionWorker>();
        var sqlEngine = rxWorker?.SqlEngine;
        if (rxWorker is null || sqlEngine is null || !rxWorker.IsSqlConnected)
        {
            await AckAsync(false, new { status = "sql_not_connected" }, "PioneerRx SQL is not connected");
            return;
        }

        var readyRxs = await sqlEngine.ReadReadyMetadataAsync(ct);
        var selected = readyRxs.Take(maxRows).ToArray();
        var exportedAt = DateTimeOffset.UtcNow;
        var fixtureJson = PioneerRxShadowFixtureExporter.Export(
            selected,
            new PioneerRxShadowFixtureExportOptions(
                SerializedAtUtc: exportedAt,
                PharmacyId: options.PharmacyId ?? "unknown",
                AgentInstallId: options.AgentId ?? "unknown",
                PmsVersion: "PioneerRx Shadow Export",
                IncludeSyntheticPatientDetails: includeSyntheticPatientDetails,
                MaxRows: maxRows));

        var fixtureDir = Path.Combine(RuntimeHealthEvidence.ProgramDataRoot, "shadow-fixtures");
        Directory.CreateDirectory(fixtureDir);
        var fileName =
            $"pioneerrx-shadow-{exportedAt:yyyyMMdd-HHmmss}-{SafeFileToken(commandId ?? command.Nonce)}.json";
        var fixturePath = Path.Combine(fixtureDir, fileName);
        WriteTextAtomically(fixturePath, fixtureJson);
        var digestPrefix = PioneerRxShadowFixtureExporter.ComputeSha256Prefix(fixtureJson);
        PioneerRxShadowReplayResult replay;
        try
        {
            replay = PioneerRxShadowReplayHarness.Load(fixturePath)
                .Replay(includeLegacyDeliveryQueue: false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "export_pioneerrx_shadow_fixture: replay gate failed");
            await AckAsync(false, new
            {
                status = "replay_failed",
                fixturePath,
                rowCount = selected.Length,
                sha256Prefix = digestPrefix,
                phiSourceValuesWritten = false
            }, "PioneerRx shadow fixture replay failed boundary checks");
            return;
        }

        var schemaCanary = rxWorker.SnapshotSchemaCanaryExportGate();
        var dashboardEvidence = ReadDashboardEvidence(dataEl);
        var releaseEvidence = ReadReleaseEvidence(dataEl);
        var track2ExitGate = BuildTrack2ExitGate(replay, schemaCanary, dashboardEvidence, releaseEvidence);

        stateDb.AppendChainedAuditEntry(new AuditEntry(
            TaskId: commandId ?? command.Nonce,
            EventType: "pioneerrx_shadow_fixture_exported",
            FromState: "accepted",
            ToState: "exported",
            Trigger: "signed_command",
            CommandId: command.Nonce,
            RequesterId: requesterId,
            Actor: "operator",
            SourceComponent: "heartbeat_worker",
            CaptureReason: "non_phi_pioneerrx_shadow_fixture"));

        await AckAsync(true, new
        {
            status = "exported",
            fixturePath,
            rowCount = selected.Length,
            sha256Prefix = digestPrefix,
            replaySha256Prefix = replay.PayloadSha256Prefix,
            candidateCount = replay.CandidateCount,
            candidateHashes = replay.RxHashes,
            schemaCanary,
            releaseGate = new
            {
                agentVersion = options.Version,
                sourceCommitMinimum = RequiredTrack2SourceCommitMinimum,
                signedReleaseRequired = true,
                releaseEvidence
            },
            track2ExitGate,
            syntheticPatientDetails = includeSyntheticPatientDetails,
            phiSourceValuesWritten = false
        }, null);
    }

    internal static bool ContainsUnsafeField(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return true;
        }

        foreach (var property in element.EnumerateObject())
        {
            var normalized = NormalizeFieldName(property.Name);

            if (normalized == "dashboardevidence")
            {
                if (IsBlockedField(property.Name) || !TryReadDashboardEvidence(property.Value, out _))
                {
                    return true;
                }

                continue;
            }

            if (normalized == "releaseevidence")
            {
                if (IsBlockedField(property.Name) || !TryReadReleaseEvidence(property.Value, out _))
                {
                    return true;
                }

                continue;
            }

            if (normalized is not ("commandid" or "requesterid" or "maxrows" or "includesyntheticpatientdetails") ||
                property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array ||
                IsBlockedField(property.Name) ||
                HasUnsafeValue(normalized, property.Value))
            {
                return true;
            }
        }

        return false;
    }

    internal static PioneerRxShadowDashboardEvidence ReadDashboardEvidence(JsonElement element)
    {
        if (!element.TryGetProperty("dashboardEvidence", out var dashboardEvidenceElement))
        {
            return PioneerRxShadowDashboardEvidence.Empty;
        }

        if (!TryReadDashboardEvidence(dashboardEvidenceElement, out var dashboardEvidence))
        {
            throw new InvalidOperationException("PioneerRx shadow dashboard evidence must be digest-backed and non-PHI.");
        }

        return dashboardEvidence;
    }

    internal static PioneerRxShadowReleaseEvidence ReadReleaseEvidence(JsonElement element)
    {
        if (!element.TryGetProperty("releaseEvidence", out var releaseEvidenceElement))
        {
            return PioneerRxShadowReleaseEvidence.Empty;
        }

        if (!TryReadReleaseEvidence(releaseEvidenceElement, out var releaseEvidence))
        {
            throw new InvalidOperationException("PioneerRx shadow release evidence must be digest-backed and non-PHI.");
        }

        return releaseEvidence;
    }

    internal static PioneerRxTrack2ExitGate BuildTrack2ExitGate(
        PioneerRxShadowReplayResult replay,
        SchemaCanaryExportGate schemaCanary,
        PioneerRxShadowDashboardEvidence dashboardEvidence,
        PioneerRxShadowReleaseEvidence releaseEvidence)
    {
        var zeroForbiddenTokens = replay.ForbiddenTokenHitCount == 0;
        var candidateRowsDashboardVisible =
            dashboardEvidence.CandidateRowsVisible &&
            IsReceiptDigest(dashboardEvidence.CandidateRowsReceiptSha256);
        var correctionPathExercised =
            dashboardEvidence.CorrectionPathExercised &&
            IsReceiptDigest(dashboardEvidence.CorrectionPathReceiptSha256);
        var dashboardEvidenceRecorded = candidateRowsDashboardVisible && correctionPathExercised;
        var releaseEvidenceRecorded =
            IsSafeReleaseTag(releaseEvidence.ReleaseTag) &&
            IsHexDigest(releaseEvidence.SourceCommit, 40) &&
            IsHexDigest(releaseEvidence.ArtifactSha256, 64) &&
            IsHexDigest(releaseEvidence.ChecksumSignatureSha256, 64) &&
            IsHexDigest(releaseEvidence.InstallReceiptSha256, 64) &&
            IsHexDigest(releaseEvidence.RollbackArtifactSha256, 64);

        return new PioneerRxTrack2ExitGate(
            ZeroForbiddenTokens: zeroForbiddenTokens,
            StableCandidateHashes: replay.StableCandidateHashes,
            SchemaCanaryRecorded: schemaCanary.Recorded,
            SchemaCanaryPassing: schemaCanary.Passing,
            CandidateRowsDashboardVisible: candidateRowsDashboardVisible,
            CorrectionPathExercised: correctionPathExercised,
            DashboardEvidenceRecorded: dashboardEvidenceRecorded,
            CandidateRowsReceiptSha256: dashboardEvidence.CandidateRowsReceiptSha256,
            CorrectionPathReceiptSha256: dashboardEvidence.CorrectionPathReceiptSha256,
            ReleaseEvidenceRecorded: releaseEvidenceRecorded,
            ReleaseTag: releaseEvidence.ReleaseTag,
            SourceCommit: releaseEvidence.SourceCommit,
            ArtifactSha256: releaseEvidence.ArtifactSha256,
            ChecksumSignatureSha256: releaseEvidence.ChecksumSignatureSha256,
            InstallReceiptSha256: releaseEvidence.InstallReceiptSha256,
            RollbackArtifactSha256: releaseEvidence.RollbackArtifactSha256,
            ReadyForTrack2Exit:
                zeroForbiddenTokens &&
                replay.StableCandidateHashes &&
                schemaCanary.Recorded &&
                schemaCanary.Passing &&
                dashboardEvidenceRecorded &&
                releaseEvidenceRecorded);
    }

    private static bool TryReadDashboardEvidence(
        JsonElement element,
        out PioneerRxShadowDashboardEvidence dashboardEvidence)
    {
        dashboardEvidence = PioneerRxShadowDashboardEvidence.Empty;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var candidateRowsVisible = false;
        var correctionPathExercised = false;
        string? candidateRowsReceiptSha256 = null;
        string? correctionPathReceiptSha256 = null;

        foreach (var property in element.EnumerateObject())
        {
            var normalized = NormalizeFieldName(property.Name);
            if (IsBlockedField(property.Name))
            {
                return false;
            }

            switch (normalized)
            {
                case "candidaterowsvisible":
                    if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    {
                        return false;
                    }

                    candidateRowsVisible = property.Value.ValueKind == JsonValueKind.True;
                    break;
                case "correctionpathexercised":
                    if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    {
                        return false;
                    }

                    correctionPathExercised = property.Value.ValueKind == JsonValueKind.True;
                    break;
                case "candidaterowsreceiptsha256":
                    candidateRowsReceiptSha256 = property.Value.ValueKind == JsonValueKind.String
                        ? property.Value.GetString()
                        : null;
                    if (!IsReceiptDigest(candidateRowsReceiptSha256))
                    {
                        return false;
                    }

                    break;
                case "correctionpathreceiptsha256":
                    correctionPathReceiptSha256 = property.Value.ValueKind == JsonValueKind.String
                        ? property.Value.GetString()
                        : null;
                    if (!IsReceiptDigest(correctionPathReceiptSha256))
                    {
                        return false;
                    }

                    break;
                default:
                    return false;
            }
        }

        if ((candidateRowsVisible && candidateRowsReceiptSha256 is null) ||
            (correctionPathExercised && correctionPathReceiptSha256 is null))
        {
            return false;
        }

        dashboardEvidence = new PioneerRxShadowDashboardEvidence(
            CandidateRowsVisible: candidateRowsVisible,
            CorrectionPathExercised: correctionPathExercised,
            CandidateRowsReceiptSha256: candidateRowsReceiptSha256,
            CorrectionPathReceiptSha256: correctionPathReceiptSha256);
        return true;
    }

    private static bool TryReadReleaseEvidence(
        JsonElement element,
        out PioneerRxShadowReleaseEvidence releaseEvidence)
    {
        releaseEvidence = PioneerRxShadowReleaseEvidence.Empty;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        string? releaseTag = null;
        string? sourceCommit = null;
        string? artifactSha256 = null;
        string? checksumSignatureSha256 = null;
        string? installReceiptSha256 = null;
        string? rollbackArtifactSha256 = null;

        foreach (var property in element.EnumerateObject())
        {
            var normalized = NormalizeFieldName(property.Name);
            if (IsBlockedField(property.Name) || property.Value.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var value = property.Value.GetString();
            switch (normalized)
            {
                case "releasetag":
                    if (!IsSafeReleaseTag(value))
                    {
                        return false;
                    }

                    releaseTag = value;
                    break;
                case "sourcecommit":
                    if (!IsHexDigest(value, 40))
                    {
                        return false;
                    }

                    sourceCommit = value;
                    break;
                case "artifactsha256":
                    if (!IsHexDigest(value, 64))
                    {
                        return false;
                    }

                    artifactSha256 = value;
                    break;
                case "checksumsignaturesha256":
                    if (!IsHexDigest(value, 64))
                    {
                        return false;
                    }

                    checksumSignatureSha256 = value;
                    break;
                case "installreceiptsha256":
                    if (!IsHexDigest(value, 64))
                    {
                        return false;
                    }

                    installReceiptSha256 = value;
                    break;
                case "rollbackartifactsha256":
                    if (!IsHexDigest(value, 64))
                    {
                        return false;
                    }

                    rollbackArtifactSha256 = value;
                    break;
                default:
                    return false;
            }
        }

        if (releaseTag is null ||
            sourceCommit is null ||
            artifactSha256 is null ||
            checksumSignatureSha256 is null ||
            installReceiptSha256 is null ||
            rollbackArtifactSha256 is null)
        {
            return false;
        }

        releaseEvidence = new PioneerRxShadowReleaseEvidence(
            ReleaseTag: releaseTag,
            SourceCommit: sourceCommit,
            ArtifactSha256: artifactSha256,
            ChecksumSignatureSha256: checksumSignatureSha256,
            InstallReceiptSha256: installReceiptSha256,
            RollbackArtifactSha256: rollbackArtifactSha256);
        return true;
    }

    private static bool HasUnsafeValue(string normalizedName, JsonElement value)
    {
        return normalizedName switch
        {
            "commandid" or "requesterid" =>
                value.ValueKind != JsonValueKind.String || !IsSafeEnvelopeToken(value.GetString()),
            "maxrows" =>
                value.ValueKind != JsonValueKind.Number ||
                !value.TryGetInt32(out var rows) ||
                rows < 0 ||
                rows > 25,
            "includesyntheticpatientdetails" =>
                value.ValueKind is not (JsonValueKind.True or JsonValueKind.False),
            _ => true,
        };
    }

    private static bool IsBlockedField(string name)
    {
        var normalized = NormalizeFieldName(name);
        return normalized is
            "text" or
            "label" or
            "windowtitle" or
            "rx" or
            "rxnumber" or
            "rxid" or
            "prescription" or
            "prescriptionid" or
            "patient" or
            "patientid" or
            "patientname" or
            "patientfirstname" or
            "patientlastname" or
            "medication" or
            "ndc" or
            "address" or
            "phone" or
            "screenshot" or
            "image" or
            "ocr" or
            "click" or
            "type" or
            "key" or
            "mouse" or
            "coordinates";
    }

    private static bool IsReceiptDigest(string? value) =>
        value is { Length: >= 16 and <= 64 } &&
        value.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsHexDigest(string? value, int length) =>
        value is not null &&
        value.Length == length &&
        value.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsSafeReleaseTag(string? value) =>
        value is { Length: > 1 and <= 64 } &&
        value[0] == 'v' &&
        value[1] is >= '0' and <= '9' &&
        value.Skip(1).All(ch => IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.');

    private static bool IsSafeEnvelopeToken(string? value) =>
        value is { Length: > 0 and <= 128 } &&
        value.All(ch => IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.' or ':');

    private static string NormalizeFieldName(string name) =>
        new(name
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());

    private static string SafeFileToken(string value)
    {
        var sanitized = new string(value
            .Where(ch => IsAsciiLetterOrDigit(ch) || ch is '-' or '_')
            .Take(64)
            .ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "command" : sanitized;
    }

    private static bool IsAsciiLetterOrDigit(char ch) =>
        ch is >= '0' and <= '9' or >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static void WriteTextAtomically(string path, string text)
    {
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(tempPath, text, Encoding.UTF8);
        if (File.Exists(path))
        {
            File.Replace(tempPath, path, null);
        }
        else
        {
            File.Move(tempPath, path);
        }
    }
}

internal sealed record PioneerRxShadowDashboardEvidence(
    [property: JsonPropertyName("candidateRowsVisible")]
    bool CandidateRowsVisible,
    [property: JsonPropertyName("correctionPathExercised")]
    bool CorrectionPathExercised,
    [property: JsonPropertyName("candidateRowsReceiptSha256")]
    string? CandidateRowsReceiptSha256,
    [property: JsonPropertyName("correctionPathReceiptSha256")]
    string? CorrectionPathReceiptSha256)
{
    public static PioneerRxShadowDashboardEvidence Empty { get; } = new(
        CandidateRowsVisible: false,
        CorrectionPathExercised: false,
        CandidateRowsReceiptSha256: null,
        CorrectionPathReceiptSha256: null);
}

internal sealed record PioneerRxShadowReleaseEvidence(
    [property: JsonPropertyName("releaseTag")]
    string? ReleaseTag,
    [property: JsonPropertyName("sourceCommit")]
    string? SourceCommit,
    [property: JsonPropertyName("artifactSha256")]
    string? ArtifactSha256,
    [property: JsonPropertyName("checksumSignatureSha256")]
    string? ChecksumSignatureSha256,
    [property: JsonPropertyName("installReceiptSha256")]
    string? InstallReceiptSha256,
    [property: JsonPropertyName("rollbackArtifactSha256")]
    string? RollbackArtifactSha256)
{
    public static PioneerRxShadowReleaseEvidence Empty { get; } = new(
        ReleaseTag: null,
        SourceCommit: null,
        ArtifactSha256: null,
        ChecksumSignatureSha256: null,
        InstallReceiptSha256: null,
        RollbackArtifactSha256: null);

    [JsonPropertyName("recorded")]
    public bool Recorded =>
        ReleaseTag is not null &&
        SourceCommit is not null &&
        ArtifactSha256 is not null &&
        ChecksumSignatureSha256 is not null &&
        InstallReceiptSha256 is not null &&
        RollbackArtifactSha256 is not null;
}

internal sealed record PioneerRxTrack2ExitGate(
    [property: JsonPropertyName("zeroForbiddenTokens")]
    bool ZeroForbiddenTokens,
    [property: JsonPropertyName("stableCandidateHashes")]
    bool StableCandidateHashes,
    [property: JsonPropertyName("schemaCanaryRecorded")]
    bool SchemaCanaryRecorded,
    [property: JsonPropertyName("schemaCanaryPassing")]
    bool SchemaCanaryPassing,
    [property: JsonPropertyName("candidateRowsDashboardVisible")]
    bool CandidateRowsDashboardVisible,
    [property: JsonPropertyName("correctionPathExercised")]
    bool CorrectionPathExercised,
    [property: JsonPropertyName("dashboardEvidenceRecorded")]
    bool DashboardEvidenceRecorded,
    [property: JsonPropertyName("candidateRowsReceiptSha256")]
    string? CandidateRowsReceiptSha256,
    [property: JsonPropertyName("correctionPathReceiptSha256")]
    string? CorrectionPathReceiptSha256,
    [property: JsonPropertyName("releaseEvidenceRecorded")]
    bool ReleaseEvidenceRecorded,
    [property: JsonPropertyName("releaseTag")]
    string? ReleaseTag,
    [property: JsonPropertyName("sourceCommit")]
    string? SourceCommit,
    [property: JsonPropertyName("artifactSha256")]
    string? ArtifactSha256,
    [property: JsonPropertyName("checksumSignatureSha256")]
    string? ChecksumSignatureSha256,
    [property: JsonPropertyName("installReceiptSha256")]
    string? InstallReceiptSha256,
    [property: JsonPropertyName("rollbackArtifactSha256")]
    string? RollbackArtifactSha256,
    [property: JsonPropertyName("readyForTrack2Exit")]
    bool ReadyForTrack2Exit);
