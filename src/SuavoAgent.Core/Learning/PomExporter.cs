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
public static class PomExporter
{
    public static string Export(AgentStateDb db, string sessionId)
    {
        var session = db.GetLearningSession(sessionId);
        if (session is null)
            throw new InvalidOperationException($"Learning session {sessionId} not found");

        var processes = db.GetObservedProcesses(sessionId);
        var schemas = db.GetDiscoveredSchemas(sessionId);
        var candidates = db.GetRxQueueCandidates(sessionId);

        var export = new
        {
            sessionId,
            pharmacyId = session.Value.PharmacyId,
            phase = session.Value.Phase,
            mode = session.Value.Mode,
            exportedAt = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd"), // day granularity only

            processes = processes.Select(p => new
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

            behavioral = new
            {
                uniqueScreens = db.GetUniqueScreenCount(sessionId),
                observationDays = ComputeObservationDays(db, sessionId),
                // TODO: droppedEventRate comes from heartbeat telemetry (BehavioralEventBuffer.DroppedEventCount
                // in the Helper process). Not available in Core at export time. Wire via IPC heartbeat in future.
                droppedEventRate = 0.0,
                screenFingerprints = db.GetDistinctTreeHashes(sessionId),
                routines = db.GetLearnedRoutines(sessionId).Select(r => new
                {
                    routineHash = r.RoutineHash,
                    path = JsonSerializer.Deserialize<JsonElement>(r.PathJson),
                    pathLength = r.PathLength,
                    frequency = r.Frequency,
                    confidence = r.Confidence,
                    hasWritebackCandidate = r.HasWritebackCandidate,
                    correlatedWriteQueries = r.CorrelatedWriteQueries is not null
                        ? JsonSerializer.Deserialize<JsonElement>(r.CorrelatedWriteQueries) : (JsonElement?)null,
                }).ToArray(),
                writebackCandidates = db.GetWritebackCandidates(sessionId).Select(c => new
                {
                    correlationKey = c.CorrelationKey,
                    elementId = c.ElementId,
                    controlType = c.ControlType,
                    queryShape = c.QueryShape,
                    tablesReferenced = TryDeserializeJson(c.TablesReferenced),
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
                        return new
                        {
                            correlationKey = a.CorrelationKey,
                            currentConfidence = a.Confidence,
                            writebackAttempts = writebackEvents.Count,
                            successRate = writebackEvents.Count > 0 ? Math.Round((double)successes / writebackEvents.Count, 2) : 0.0,
                            operatorApproved = ext?.OperatorApproved ?? false,
                            promotionSuspended = ext?.PromotionSuspended ?? false,
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

    private static JsonElement? TryDeserializeJson(string? json)
    {
        if (json is null) return null;
        try { return JsonSerializer.Deserialize<JsonElement>(json); }
        catch { return null; }
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
