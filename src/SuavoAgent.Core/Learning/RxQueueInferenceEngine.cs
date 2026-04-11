using System.Text.Json;
using System.Text.RegularExpressions;
using SuavoAgent.Core.State;

namespace SuavoAgent.Core.Learning;

/// <summary>
/// Scores discovered tables as potential Rx queue candidates using
/// schema structure, column names, and access pattern heuristics.
/// Runs locally during the Model phase. Never touches row data.
/// </summary>
public sealed partial class RxQueueInferenceEngine
{
    private readonly AgentStateDb _db;

    [GeneratedRegex(@"rx.*num|rxnumber", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex RxNumberStrongPattern();

    [GeneratedRegex(@"prescription.*id|rx.*id", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex RxNumberWeakPattern();

    [GeneratedRegex(@"status|state|workflow", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex StatusPattern();

    [GeneratedRegex(@"patient.*id|person.*id", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex PatientFkPattern();

    public RxQueueInferenceEngine(AgentStateDb db)
    {
        _db = db;
    }

    public record RxCandidate(
        string PrimaryTable,
        string? RxNumberColumn,
        string? StatusColumn,
        string? DateColumn,
        string? PatientFkColumn,
        double Confidence,
        List<string> Evidence,
        List<string> NegativeEvidence);

    public IReadOnlyList<RxCandidate> InferCandidates(string sessionId)
    {
        var schemas = _db.GetDiscoveredSchemas(sessionId);

        // Group columns by schema.table
        var tables = new Dictionary<string, List<(string Column, string DataType, string? Purpose)>>();
        foreach (var col in schemas)
        {
            var key = $"{col.SchemaName}.{col.TableName}";
            if (!tables.ContainsKey(key))
                tables[key] = new();
            tables[key].Add((col.ColumnName, col.DataType, col.InferredPurpose));
        }

        var candidates = new List<RxCandidate>();

        foreach (var (table, columns) in tables)
        {
            double confidence = 0;
            var evidence = new List<string>();
            var negEvidence = new List<string>();
            string? rxCol = null, statusCol = null, dateCol = null, patientCol = null;

            // Rx number column? +0.3 (prefer strong pattern like RxNumber over weak like RxTransactionID)
            var rxMatch = columns.FirstOrDefault(c => RxNumberStrongPattern().IsMatch(c.Column));
            if (rxMatch == default)
                rxMatch = columns.FirstOrDefault(c => RxNumberWeakPattern().IsMatch(c.Column));
            if (rxMatch != default)
            {
                confidence += 0.3;
                rxCol = rxMatch.Column;
                evidence.Add($"Column '{rxCol}' matches Rx number pattern");
            }

            // Status column? +0.2
            var statusMatch = columns.FirstOrDefault(c => StatusPattern().IsMatch(c.Column));
            if (statusMatch != default)
            {
                confidence += 0.2;
                statusCol = statusMatch.Column;
                evidence.Add($"Column '{statusCol}' matches status pattern");
            }

            // Temporal column? +0.1
            var dateMatch = columns.FirstOrDefault(c =>
                c.Purpose == "temporal" ||
                c.DataType.Contains("date", StringComparison.OrdinalIgnoreCase) ||
                c.DataType.Contains("time", StringComparison.OrdinalIgnoreCase));
            if (dateMatch != default)
            {
                confidence += 0.1;
                dateCol = dateMatch.Column;
                evidence.Add($"Column '{dateCol}' is temporal ({dateMatch.DataType})");
            }

            // Patient FK? +0.1 (also marks PHI fence)
            var patientMatch = columns.FirstOrDefault(c => PatientFkPattern().IsMatch(c.Column));
            if (patientMatch != default)
            {
                confidence += 0.1;
                patientCol = patientMatch.Column;
                evidence.Add($"Column '{patientCol}' is patient FK (PHI fence)");
            }

            // Negative evidence
            if (columns.Count < 3)
                negEvidence.Add($"Table has only {columns.Count} columns (unusually few for Rx queue)");
            if (table.Contains("Log", StringComparison.OrdinalIgnoreCase) ||
                table.Contains("Audit", StringComparison.OrdinalIgnoreCase) ||
                table.Contains("History", StringComparison.OrdinalIgnoreCase))
                negEvidence.Add("Table name suggests log/audit/history, not active queue");

            if (confidence > 0)
            {
                candidates.Add(new RxCandidate(table, rxCol, statusCol, dateCol,
                    patientCol, Math.Round(confidence, 2), evidence, negEvidence));
            }
        }

        return candidates.OrderByDescending(c => c.Confidence).ToList();
    }

    public void InferAndPersist(string sessionId)
    {
        var candidates = InferCandidates(sessionId);
        foreach (var c in candidates)
        {
            _db.InsertRxQueueCandidate(sessionId, c.PrimaryTable,
                c.RxNumberColumn, c.StatusColumn, c.DateColumn, c.PatientFkColumn,
                c.Confidence,
                JsonSerializer.Serialize(c.Evidence),
                c.NegativeEvidence.Count > 0 ? JsonSerializer.Serialize(c.NegativeEvidence) : null);
        }
    }
}
