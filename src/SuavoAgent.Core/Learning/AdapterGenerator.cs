using System.Text;
using Microsoft.Extensions.Logging;
using SuavoAgent.Core.State;

namespace SuavoAgent.Core.Learning;

/// <summary>
/// Generates a LearnedPmsAdapter from an approved POM.
/// Reads the highest-confidence Rx queue candidate and delivery-ready statuses,
/// builds a parameterized detection query, and wires it into the adapter.
/// </summary>
public sealed class AdapterGenerator
{
    private readonly AgentStateDb _db;
    private const double MinConfidence = 0.6;

    public AdapterGenerator(AgentStateDb db) => _db = db;

    public LearnedPmsAdapter? Generate(string sessionId, string? connectionString = null,
        ILogger? logger = null)
    {
        var candidates = _db.GetRxQueueCandidates(sessionId);
        var best = candidates.FirstOrDefault(c => c.Confidence >= MinConfidence);

        if (best.PrimaryTable is null)
            return null;

        if (string.IsNullOrEmpty(best.RxNumberColumn) || string.IsNullOrEmpty(best.StatusColumn))
            return null;

        var statuses = _db.GetDiscoveredStatuses(sessionId);
        var deliveryReady = StatusOrderingEngine.GetDeliveryReadyValues(statuses);

        if (deliveryReady.Count == 0)
            return null;

        var query = BuildDetectionQuery(best.PrimaryTable, best.RxNumberColumn,
            best.StatusColumn, best.DateColumn, deliveryReady);

        return new LearnedPmsAdapter(
            pmsName: $"Learned-{best.PrimaryTable}",
            connectionString: connectionString ?? "",
            detectionQuery: query,
            rxNumberColumn: best.RxNumberColumn,
            statusColumn: best.StatusColumn,
            deliveryReadyStatuses: deliveryReady,
            logger: logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
    }

    internal static string BuildDetectionQuery(string table, string rxNumberColumn,
        string statusColumn, string? dateColumn, IReadOnlyList<string> statusValues)
    {
        var sb = new StringBuilder();
        sb.AppendLine("SELECT TOP 50");
        sb.AppendLine($"    [{rxNumberColumn}], [{statusColumn}]");
        if (dateColumn != null)
            sb.AppendLine($"    , [{dateColumn}]");
        sb.AppendLine($"FROM {table}");
        sb.Append($"WHERE [{statusColumn}] IN (");
        sb.AppendJoin(", ", statusValues.Select(v => $"'{v}'"));
        sb.Append(')');
        return sb.ToString();
    }
}
