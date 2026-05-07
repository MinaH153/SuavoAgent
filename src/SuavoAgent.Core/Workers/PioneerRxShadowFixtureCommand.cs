using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Health;
using SuavoAgent.Core.State;

namespace SuavoAgent.Core.Workers;

internal static class PioneerRxShadowFixtureCommand
{
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

    private static bool HasUnsafeValue(string normalizedName, JsonElement value)
    {
        return normalizedName switch
        {
            "commandid" or "requesterid" =>
                value.ValueKind != JsonValueKind.String || value.GetString()?.Length > 128,
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

    private static string NormalizeFieldName(string name) =>
        new(name
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());

    private static string SafeFileToken(string value)
    {
        var sanitized = new string(value
            .Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_')
            .Take(64)
            .ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "command" : sanitized;
    }

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
