using System.Text.RegularExpressions;

namespace SuavoAgent.Core.Learning;

/// <summary>
/// Fail-closed SQL text normalizer. Extracts query shape and table references
/// from SQL text without persisting any literal values that could contain PHI.
///
/// CRITICAL: Raw SQL text from sys.dm_exec_sql_text is TOXIC — it may contain
/// patient names, DOBs, Rx numbers as string/numeric literals. This tokenizer
/// parses structure only. If it cannot safely classify all tokens, it DISCARDS
/// the entire statement (returns null). Never persist what you can't parse.
/// </summary>
public static partial class SqlTokenizer
{
    public record NormalizedQuery(
        string NormalizedShape,
        IReadOnlyList<string> TablesReferenced,
        bool IsWrite);

    // Allowlisted statement types — everything else is discarded
    private static readonly HashSet<string> AllowedKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "INSERT", "UPDATE", "DELETE"
    };

    // DDL/EXEC = discard
    private static readonly HashSet<string> BlockedKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "CREATE", "ALTER", "DROP", "TRUNCATE", "EXEC", "EXECUTE", "GRANT", "REVOKE", "DENY"
    };

    // Table reference: schema.table or just table (2-part names)
    [GeneratedRegex(@"(?:FROM|JOIN|INTO|UPDATE)\s+(?:\[?(\w+)\]?\.)?(?:\[?(\w+)\]?)(?:\s|$|,|\()",
        RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex TableRefPattern();

    // String literal: 'anything'
    [GeneratedRegex(@"'[^']*'", RegexOptions.Compiled)]
    private static partial Regex StringLiteralPattern();

    // Numeric literal not preceded by @: bare numbers that could be MRNs/Rx numbers
    [GeneratedRegex(@"(?<!@)\b(\d{3,})\b", RegexOptions.Compiled)]
    private static partial Regex NumericLiteralPattern();

    // Parameter names: @p1, @paramName, etc.
    [GeneratedRegex(@"@\w+", RegexOptions.Compiled)]
    private static partial Regex ParameterPattern();

    public static NormalizedQuery? TryNormalize(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql)) return null;

        var trimmed = sql.Trim();

        // Extract first keyword
        var firstWord = trimmed.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries)[0]
            .TrimStart('(');

        // Block DDL and EXEC
        if (BlockedKeywords.Contains(firstWord)) return null;

        // Only allow known DML
        if (!AllowedKeywords.Contains(firstWord)) return null;

        // Fail-closed: reject if string literals present (may contain patient names)
        if (StringLiteralPattern().IsMatch(trimmed)) return null;

        // Fail-closed: reject if bare numeric literals present (may contain MRN/Rx numbers)
        if (NumericLiteralPattern().IsMatch(trimmed)) return null;

        // Extract table references
        var tables = new List<string>();
        foreach (Match m in TableRefPattern().Matches(trimmed))
        {
            var schema = m.Groups[1].Success ? m.Groups[1].Value : "";
            var table = m.Groups[2].Value;
            var fullName = string.IsNullOrEmpty(schema) ? table : $"{schema}.{table}";
            if (!tables.Contains(fullName))
                tables.Add(fullName);
        }

        if (tables.Count == 0) return null;

        // Build normalized shape: replace parameter names with @p
        var shape = ParameterPattern().Replace(trimmed, "@p");
        var isWrite = firstWord.Equals("INSERT", StringComparison.OrdinalIgnoreCase)
                   || firstWord.Equals("UPDATE", StringComparison.OrdinalIgnoreCase)
                   || firstWord.Equals("DELETE", StringComparison.OrdinalIgnoreCase);

        return new NormalizedQuery(shape, tables, isWrite);
    }
}
