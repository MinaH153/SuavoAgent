using System.Text;
using System.Text.RegularExpressions;
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

    // Only word characters (letters, digits, underscore) with exactly one dot separator
    private static readonly Regex SafeTableNamePattern = new(@"^[\w]+\.[\w]+$", RegexOptions.Compiled);

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

        var statuses = _db.GetDiscoveredStatusesForTable(sessionId, best.PrimaryTable);
        var deliveryReady = StatusOrderingEngine.GetDeliveryReadyValues(statuses);

        if (deliveryReady.Count == 0)
            return null;

        var result = BuildDetectionQuery(best.PrimaryTable, best.RxNumberColumn,
            best.StatusColumn, best.DateColumn, deliveryReady);

        if (result is null)
            return null;

        return new LearnedPmsAdapter(
            pmsName: $"Learned-{best.PrimaryTable}",
            connectionString: connectionString ?? "",
            detectionQuery: result.Value.Query,
            statusParameters: result.Value.Parameters,
            rxNumberColumn: best.RxNumberColumn,
            statusColumn: best.StatusColumn,
            deliveryReadyStatuses: deliveryReady,
            logger: logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
    }

    /// <summary>
    /// Result of building a parameterized detection query.
    /// Query contains @s0, @s1, ... placeholders; Parameters maps names to values.
    /// </summary>
    public readonly record struct ParameterizedQuery(
        string Query,
        IReadOnlyDictionary<string, string> Parameters);

    /// <summary>
    /// Bracket-escapes a SQL identifier by replacing ] with ]] and wrapping in [].
    /// </summary>
    internal static string BracketEscape(string identifier)
        => $"[{identifier.Replace("]", "]]")}]";

    internal static ParameterizedQuery? BuildDetectionQuery(string table, string rxNumberColumn,
        string statusColumn, string? dateColumn, IReadOnlyList<string> statusValues)
    {
        // Validate table name: must be schema.table with only word characters
        if (!SafeTableNamePattern.IsMatch(table))
            return null;

        var parts = table.Split('.');
        var safeTable = $"{BracketEscape(parts[0])}.{BracketEscape(parts[1])}";

        var sb = new StringBuilder();
        sb.AppendLine("SELECT TOP 50");
        sb.AppendLine($"    {BracketEscape(rxNumberColumn)}, {BracketEscape(statusColumn)}");
        if (dateColumn != null)
            sb.AppendLine($"    , {BracketEscape(dateColumn)}");
        sb.AppendLine($"FROM {safeTable}");

        // Generate parameter placeholders instead of inline values
        var parameters = new Dictionary<string, string>(statusValues.Count);
        var placeholders = new string[statusValues.Count];
        for (var i = 0; i < statusValues.Count; i++)
        {
            var paramName = $"@s{i}";
            placeholders[i] = paramName;
            parameters[paramName] = statusValues[i];
        }

        sb.Append($"WHERE {BracketEscape(statusColumn)} IN (");
        sb.AppendJoin(", ", placeholders);
        sb.Append(')');

        return new ParameterizedQuery(sb.ToString(), parameters);
    }
}
