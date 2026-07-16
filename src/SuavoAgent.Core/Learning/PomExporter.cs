using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SuavoAgent.Core.Behavioral;
using SuavoAgent.Core.State;

namespace SuavoAgent.Core.Learning;

/// <summary>
/// Exports the Pharmacy Operations Model in a de-identified format for cloud upload.
/// Strips all ePHI artifacts: HMAC hashes, exact timestamps, file paths, credentials.
/// The export is suitable for dashboard review and operator approval.
///
/// Per Codex CRITICAL-2: ComputeDigest produces the approved_model_digest that
/// binds the approval to the exact reviewed model.
/// </summary>
public class PomExporter
{
    private static readonly Dictionary<string, string> KnownPmsProcesses = new(StringComparer.Ordinal)
    {
        ["PioneerPharmacy.exe"] = "pioneerrx",
        ["QS1NexGen.exe"] = "qs1_nexgen",
        ["NexGen.exe"] = "qs1_nexgen",
        ["LibertyRx.exe"] = "liberty",
        ["ComputerRx.exe"] = "computer_rx",
        ["BestRx.exe"] = "bestrx",
        ["Rx30.exe"] = "rx30",
        ["Pharmaserv.exe"] = "pharmaserv",
        ["FrameworkLTC.exe"] = "framework_ltc",
        ["ScriptPro.exe"] = "scriptpro",
    };

    private sealed record CloudRoutinePathStep(
        string TreeHash,
        string ElementToken,
        string? ControlTypeToken,
        string? QueryShapeHash);

    private readonly AgentStateDb _db;
    private readonly string _sessionId;
    private readonly string _pharmacyId;
    private readonly string? _pmsVersionHash;
    private readonly long _droppedEventCount;

    public PomExporter(AgentStateDb db, string sessionId, string pharmacyId,
        string? pmsVersionHash = null, long droppedEventCount = 0)
    {
        _db = db;
        _sessionId = sessionId;
        _pharmacyId = pharmacyId;
        _pmsVersionHash = pmsVersionHash;
        _droppedEventCount = droppedEventCount;
    }

    /// <summary>
    /// Instance-based export returning (json, digest) tuple.
    /// </summary>
    public (string Json, string Digest) Export()
    {
        var json = ExportCore(_db, _sessionId, _pmsVersionHash, _droppedEventCount);
        var digest = ComputeDigest(_pharmacyId, _sessionId, json);
        return (json, digest);
    }

    /// <summary>
    /// Static overload for backward compatibility — existing callers pass no pmsVersionHash.
    /// </summary>
    public static string Export(AgentStateDb db, string sessionId,
        string? pmsVersionHash = null, long droppedEventCount = 0)
    {
        return ExportCore(db, sessionId, pmsVersionHash, droppedEventCount);
    }

    private static string ExportCore(AgentStateDb db, string sessionId,
        string? pmsVersionHash, long droppedEventCount = 0)
    {
        var session = db.GetLearningSession(sessionId);
        if (session is null)
            throw new InvalidOperationException($"Learning session {sessionId} not found");

        var processes = db.GetObservedProcesses(sessionId);
        var cloudPmsProcesses = GetCloudPmsProcesses(processes);
        var schemas = db.GetDiscoveredSchemas(sessionId);
        var candidates = db.GetRxQueueCandidates(sessionId);
        var adapterTemplate = new AdapterGenerator(db).Describe(sessionId);

        var export = new
        {
            schemaVersion = 1,
            sessionId,
            pharmacyId = session.Value.PharmacyId,
            phase = session.Value.Phase,
            mode = session.Value.Mode,
            exportedAt = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd"), // day granularity only

            // Export only an allowlisted PMS identity. Arbitrary host process
            // inventory is not needed for approval and can become an
            // exfiltration channel for workstation/user-specific names.
            processes = cloudPmsProcesses.Select(p => new
            {
                processName = p.ProcessName,
                // exePath STRIPPED — may reveal pharmacy directory structure
                isPmsCandidate = p.IsPmsCandidate,
                occurrenceCount = p.OccurrenceCount,
                // windowTitleHash STRIPPED
                // windowTitleScrubbed STRIPPED (may contain residual PHI)
            }).ToArray(),

            schemas = schemas.Select(s => new
            {
                // serverHash STRIPPED
                schemaName = s.SchemaName,
                tableName = s.TableName,
                columnName = s.ColumnName,
                dataType = s.DataType,
                isPk = s.IsPk,
                isFk = s.IsFk,
                inferredPurpose = s.InferredPurpose,
            }).ToArray(),

            rxQueueCandidates = candidates.Select(c => new
            {
                primaryTable = c.PrimaryTable,
                rxNumberColumn = c.RxNumberColumn,
                statusColumn = c.StatusColumn,
                dateColumn = c.DateColumn,
                patientFkColumn = c.PatientFkColumn,
                confidence = c.Confidence,
                evidence = c.EvidenceJson,
            }).ToArray(),

            // Exact read template proposed for activation. All fields are schema/status
            // metadata (never row values or patient data). The template digest includes
            // session_id and every query-affecting value; the outer POM digest therefore
            // binds the human approval to this exact adapter configuration.
            learnedAdapterTemplate = adapterTemplate is null ? null : new
            {
                sessionId = adapterTemplate.SessionId,
                templateDigest = adapterTemplate.TemplateDigest,
                sourceIdentityDigest = adapterTemplate.SourceIdentityDigest,
                databaseName = adapterTemplate.DatabaseName,
                schemaContractDigest = adapterTemplate.SchemaContractDigest,
                pmsName = adapterTemplate.PmsName,
                detectionQuery = adapterTemplate.DetectionQuery,
                statusParameters = adapterTemplate.StatusParameters,
                detectionValidationQuery = adapterTemplate.DetectionValidationQuery,
                detectionValidationParameters = adapterTemplate.DetectionValidationParameters,
                rxNumberColumn = adapterTemplate.RxNumberColumn,
                statusColumn = adapterTemplate.StatusColumn,
                deliveryReadyStatuses = adapterTemplate.DeliveryReadyStatuses,
                patientLookupQuery = adapterTemplate.PatientLookupQuery,
                patientLookupValidationQuery = adapterTemplate.PatientLookupValidationQuery,
                patientLookupValidationParameters = adapterTemplate.PatientLookupValidationParameters,
            },

            behavioral = new
            {
                pmsVersionHash = NormalizeOptionalHash(sessionId, pmsVersionHash),
                uniqueScreens = db.GetUniqueScreenCount(sessionId),
                observationDays = ComputeObservationDays(db, sessionId),
                droppedEventCount,
                screenFingerprints = db.GetDistinctTreeHashes(sessionId)
                    .Select(value => NormalizeHash(sessionId, value))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray(),
                routines = db.GetLearnedRoutines(sessionId).Select(r => new
                {
                    routineHash = NormalizeHash(sessionId, r.RoutineHash),
                    path = ParseRoutinePath(sessionId, r.PathJson),
                    pathLength = r.PathLength,
                    frequency = r.Frequency,
                    confidence = r.Confidence,
                    hasWritebackCandidate = r.HasWritebackCandidate,
                    correlatedWriteQueries = ParseHashArray(sessionId, r.CorrelatedWriteQueries),
                }).ToArray(),
                writebackCandidates = db.GetWritebackCandidates(sessionId).Select(c => new
                {
                    correlationToken = HashStructuralToken(sessionId, c.CorrelationKey),
                    elementToken = HashStructuralToken(sessionId, c.ElementId),
                    controlTypeToken = c.ControlType is null
                        ? null : HashStructuralToken(sessionId, c.ControlType),
                    queryShape = c.QueryShape,
                    tablesReferenced = ParseTablesReferenced(c.TablesReferenced),
                    occurrences = c.OccurrenceCount,
                    confidence = c.Confidence,
                }).ToArray(),
                dmvAccess = db.GetDmvQueryObservations(sessionId, 1).Count > 0,
                totalInteractions = db.GetBehavioralEventCount(sessionId, "interaction"),
            },

            feedback = new
            {
                totalFeedbackEvents = db.GetFeedbackEventCount(sessionId),
                confidenceTrajectory = db.GetCorrelatedActions(sessionId)
                    .Where(a => a.IsWrite)
                    .Select(a =>
                    {
                        var ext = db.GetCorrelatedActionExtended(sessionId, a.CorrelationKey);
                        var writebackEvents = db.GetFeedbackEventsForTarget(sessionId, a.CorrelationKey, "writeback");
                        var successes = writebackEvents.Count(e => e.PayloadJson?.Contains("\"outcome\":\"success\"") == true);
                        var source = db.GetCorrelatedActionSource(sessionId, a.CorrelationKey);
                        return new
                        {
                            correlationToken = HashStructuralToken(sessionId, a.CorrelationKey),
                            currentConfidence = a.Confidence,
                            writebackAttempts = writebackEvents.Count,
                            successRate = writebackEvents.Count > 0 ? Math.Round((double)successes / writebackEvents.Count, 2) : 0.0,
                            operatorApproved = ext?.OperatorApproved ?? false,
                            promotionSuspended = ext?.PromotionSuspended ?? false,
                            origin = source.Source,
                            firstSeedDigest = NormalizeOptionalHash(sessionId, source.SeedDigest),
                            seededAt = source.SeededAt,
                        };
                    }).ToArray(),
                windowOverrides = db.GetWindowOverrideCount(sessionId),
                staleCorrelations = db.GetExpiredStaleCorrelations(sessionId, 0).Count,
            },
        };

        return JsonSerializer.Serialize(export, new JsonSerializerOptions
        {
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });
    }

    private static CloudRoutinePathStep[] ParseRoutinePath(string sessionId, string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return [];

            var result = new List<CloudRoutinePathStep>();
            foreach (var step in document.RootElement.EnumerateArray())
            {
                if (step.ValueKind != JsonValueKind.Object ||
                    !TryGetString(step, "treeHash", out var treeHash) ||
                    !TryGetString(step, "elementId", out var elementId))
                {
                    return [];
                }

                TryGetNullableString(step, "controlType", out var controlType);
                TryGetNullableString(step, "queryShapeHash", out var queryShapeHash);
                result.Add(new CloudRoutinePathStep(
                    NormalizeHash(sessionId, treeHash),
                    HashStructuralToken(sessionId, elementId),
                    controlType is null ? null : HashStructuralToken(sessionId, controlType),
                    NormalizeOptionalHash(sessionId, queryShapeHash)));
            }
            return result.ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static IReadOnlyList<(string ProcessName, bool IsPmsCandidate, int OccurrenceCount)>
        GetCloudPmsProcesses(
            IReadOnlyList<(string ProcessName, string ExePath, string? WindowTitleScrubbed,
                int OccurrenceCount, bool IsPmsCandidate)> processes)
    {
        var candidates = processes
            .Where(p => p.IsPmsCandidate && KnownPmsProcesses.ContainsKey(p.ProcessName))
            .ToArray();
        var pmsKeys = candidates
            .Select(p => KnownPmsProcesses[p.ProcessName])
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (pmsKeys.Length > 1)
        {
            throw new InvalidOperationException(
                "POM export refused because more than one PMS identity was observed");
        }

        return candidates
            .Select(p => (p.ProcessName, p.IsPmsCandidate, p.OccurrenceCount))
            .ToArray();
    }

    private static string[]? ParseHashArray(string sessionId, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        IEnumerable<string> items;
        try
        {
            using var document = JsonDocument.Parse(value);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return null;
            items = document.RootElement.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!)
                .ToArray();
        }
        catch (JsonException)
        {
            // Compatibility with pre-v1 rows, which stored a comma-delimited
            // list. The cloud still receives only normalized SHA-256 tokens.
            items = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        var hashes = items
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => NormalizeHash(sessionId, item))
            .Distinct(StringComparer.Ordinal)
            .Take(20)
            .ToArray();
        return hashes.Length == 0 ? null : hashes;
    }

    private static string[]? ParseTablesReferenced(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            using var document = JsonDocument.Parse(value);
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                var parsed = document.RootElement.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString()!)
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Distinct(StringComparer.Ordinal)
                    .Take(64)
                    .ToArray();
                return parsed.Length == 0 ? null : parsed;
            }
        }
        catch (JsonException)
        {
            // Older rows used a single table identifier or comma list.
        }

        var legacy = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .Take(64)
            .ToArray();
        return legacy.Length == 0 ? null : legacy;
    }

    private static bool TryGetString(JsonElement value, string propertyName, out string result)
    {
        result = string.Empty;
        return value.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.String &&
            (result = property.GetString() ?? string.Empty).Length > 0;
    }

    private static bool TryGetNullableString(
        JsonElement value,
        string propertyName,
        out string? result)
    {
        result = null;
        if (!value.TryGetProperty(propertyName, out var property) ||
            property.ValueKind == JsonValueKind.Null)
        {
            return true;
        }
        if (property.ValueKind != JsonValueKind.String)
            return false;
        result = property.GetString();
        return true;
    }

    private static string? NormalizeOptionalHash(string sessionId, string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : NormalizeHash(sessionId, value);

    private static string NormalizeHash(string sessionId, string value)
    {
        if (value.Length == 64 && value.All(Uri.IsHexDigit))
            return value.ToLowerInvariant();
        return HashStructuralToken(sessionId, value);
    }

    private static string HashStructuralToken(string sessionId, string value)
    {
        var input = $"{sessionId}\n{value}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)))
            .ToLowerInvariant();
    }

    private static double ComputeObservationDays(AgentStateDb db, string sessionId)
    {
        var firstTimestamp = db.GetFirstBehavioralEventTimestamp(sessionId);
        if (firstTimestamp is null) return 0.0;
        if (!DateTimeOffset.TryParse(firstTimestamp, null,
            System.Globalization.DateTimeStyles.RoundtripKind, out var firstSeen))
            return 0.0;
        return Math.Round((DateTimeOffset.UtcNow - firstSeen).TotalDays, 2);
    }

    /// <summary>
    /// Computes SHA-256 digest over {pharmacyId, sessionId, pomJson}.
    /// This digest is signed by the cloud during approval and verified by the agent
    /// before activating the model (TOCTOU protection — Codex CRITICAL-2).
    /// </summary>
    public static string ComputeDigest(string pharmacyId, string sessionId, string pomJson)
    {
        var input = $"{pharmacyId}|{sessionId}|{pomJson}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
