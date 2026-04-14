using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SuavoAgent.Core.State;

namespace SuavoAgent.Core.Behavioral;

/// <summary>
/// Mines repeatable action sequences from behavioral events using a directly-follows graph (DFG).
/// Discovers routines the pharmacist performs habitually — enabling writeback prediction
/// and workflow automation candidates.
/// </summary>
public sealed class RoutineDetector
{
    private record ActionNode(string TreeHash, string ElementId, string? ControlType);

    private record PathStep(string treeHash, string elementId, string? controlType, string? queryShapeHash);

    private const int MinFrequency = 5;
    private const int MaxPathLength = 20;
    private const int MinPathLength = 3;
    private const double HighConfidenceThreshold = 10;
    private const double MidConfidenceThreshold = 5;
    private const double HighConfidenceValue = 0.9;
    private const double MidConfidenceValue = 0.7;
    private const double LowConfidenceValue = 0.3;
    private static readonly TimeSpan MaxEdgeGap = TimeSpan.FromSeconds(30);

    private readonly AgentStateDb _db;
    private readonly string _sessionId;

    public RoutineDetector(AgentStateDb db, string sessionId)
    {
        _db = db;
        _sessionId = sessionId;
    }

    public void DetectAndPersist()
    {
        var events = _db.GetBehavioralEvents(_sessionId, "interaction", limit: 50000);
        if (events.Count < MinPathLength)
            return;

        // Build directly-follows graph: edge (A→B) with frequency count
        var dfg = new Dictionary<(ActionNode From, ActionNode To), int>();

        for (int i = 0; i < events.Count - 1; i++)
        {
            var curr = events[i];
            var next = events[i + 1];

            if (curr.TreeHash is null || curr.ElementId is null ||
                next.TreeHash is null || next.ElementId is null)
                continue;

            var currTs = DateTimeOffset.Parse(curr.HelperTimestamp);
            var nextTs = DateTimeOffset.Parse(next.HelperTimestamp);
            var gap = nextTs - currTs;

            if (gap < TimeSpan.Zero || gap > MaxEdgeGap)
                continue;

            var from = new ActionNode(curr.TreeHash, curr.ElementId, curr.ControlType);
            var to = new ActionNode(next.TreeHash, next.ElementId, next.ControlType);
            var edge = (from, to);

            dfg[edge] = dfg.TryGetValue(edge, out var count) ? count + 1 : 1;
        }

        // Filter to frequent edges
        var frequentEdges = new Dictionary<(ActionNode From, ActionNode To), int>(
            dfg.Where(kv => kv.Value >= MinFrequency)
               .ToDictionary(kv => kv.Key, kv => kv.Value));

        if (frequentEdges.Count == 0)
            return;

        // Build adjacency list: node → list of (successor, frequency)
        var outgoing = new Dictionary<ActionNode, List<(ActionNode Node, int Freq)>>();
        var hasIncoming = new HashSet<ActionNode>();

        foreach (var ((from, to), freq) in frequentEdges)
        {
            if (!outgoing.ContainsKey(from))
                outgoing[from] = new List<(ActionNode, int)>();
            outgoing[from].Add((to, freq));
            hasIncoming.Add(to);
        }

        // Start nodes: appear in frequent edges but have no frequent incoming edge
        var allNodes = outgoing.Keys.ToHashSet();
        var startNodes = allNodes.Where(n => !hasIncoming.Contains(n)).ToList();

        // Load correlated write actions for writeback candidate detection
        var correlatedActions = _db.GetCorrelatedActions(_sessionId);
        var writebackNodeKeys = new HashSet<string>(
            correlatedActions
                .Where(a => a.IsWrite)
                .Select(a => $"{a.TreeHash}:{a.ElementId}"));

        // ElementId → QueryShapeHash map for write nodes
        var writeQueryMap = new Dictionary<string, string?>(
            correlatedActions
                .Where(a => a.IsWrite && a.QueryShapeHash is not null)
                .GroupBy(a => $"{a.TreeHash}:{a.ElementId}")
                .ToDictionary(g => g.Key, g => g.First().QueryShapeHash));

        // Traverse from each start node, following highest-frequency edges
        foreach (var start in startNodes)
        {
            var path = new List<ActionNode>();
            var visited = new HashSet<ActionNode>();
            var node = start;

            while (path.Count < MaxPathLength)
            {
                if (visited.Contains(node))
                    break; // cycle detected

                path.Add(node);
                visited.Add(node);

                if (!outgoing.TryGetValue(node, out var successors) || successors.Count == 0)
                    break;

                // Follow highest-frequency outgoing edge
                node = successors.MaxBy(s => s.Freq).Node;
            }

            if (path.Count < MinPathLength)
                continue;

            // Compute min edge frequency along the path
            int minFreq = int.MaxValue;
            for (int i = 0; i < path.Count - 1; i++)
            {
                var edge = (path[i], path[i + 1]);
                if (frequentEdges.TryGetValue(edge, out var f))
                    minFreq = Math.Min(minFreq, f);
                else
                {
                    minFreq = 0;
                    break;
                }
            }

            if (minFreq < MinFrequency)
                continue;

            // Check writeback candidates and collect write query shape hashes
            bool hasWritebackCandidate = false;
            var writeQueryHashes = new List<string>();

            foreach (var n in path)
            {
                var key = $"{n.TreeHash}:{n.ElementId}";
                if (writebackNodeKeys.Contains(key))
                {
                    hasWritebackCandidate = true;
                    if (writeQueryMap.TryGetValue(key, out var qsh) && qsh is not null)
                        writeQueryHashes.Add(qsh);
                }
            }

            // Compute confidence
            double confidence = minFreq >= HighConfidenceThreshold
                ? HighConfidenceValue
                : minFreq >= MidConfidenceThreshold
                    ? MidConfidenceValue
                    : LowConfidenceValue;

            // Build path JSON
            var pathSteps = path.Select(n =>
            {
                var key = $"{n.TreeHash}:{n.ElementId}";
                string? qsh = writeQueryMap.TryGetValue(key, out var v) ? v : null;
                return new PathStep(n.TreeHash, n.ElementId, n.ControlType, qsh);
            }).ToList();

            string pathJson = JsonSerializer.Serialize(pathSteps, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            // Compute routine hash: SHA-256 of "treeHash:elementId|" pairs
            var hashInput = string.Concat(path.Select(n => $"{n.TreeHash}:{n.ElementId}|"));
            var routineHash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(hashInput))).ToLowerInvariant();

            string? startElementId = path.First().ElementId;
            string? endElementId = path.Last().ElementId;
            string? correlatedWriteQueries = writeQueryHashes.Count > 0
                ? string.Join(",", writeQueryHashes)
                : null;

            _db.UpsertLearnedRoutine(
                _sessionId,
                routineHash,
                pathJson,
                path.Count,
                minFreq,
                confidence,
                startElementId,
                endElementId,
                correlatedWriteQueries,
                hasWritebackCandidate);
        }
    }
}
