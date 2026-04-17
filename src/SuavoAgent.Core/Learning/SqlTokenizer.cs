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

    // DDL/EXEC/remote/encoding functions = discard
    private static readonly HashSet<string> BlockedKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "CREATE", "ALTER", "DROP", "TRUNCATE", "EXEC", "EXECUTE", "GRANT", "REVOKE", "DENY",
        "OPENQUERY", "OPENROWSET", "OPENDATASOURCE",
        "CHAR", "NCHAR"  // ASCII/Unicode codepoint encoding — bypass path for PHI in short numerics
    };

    // Remote data source functions — block even mid-query (they appear as function calls)
    private static readonly HashSet<string> RemoteFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "OPENQUERY", "OPENROWSET", "OPENDATASOURCE"
    };

    // -----------------------------------------------------------------------
    // Comment patterns (strip before any other check)
    // -----------------------------------------------------------------------

    [GeneratedRegex(@"--.*$", RegexOptions.Multiline | RegexOptions.Compiled)]
    private static partial Regex LineCommentPattern();

    [GeneratedRegex(@"/\*[\s\S]*?\*/", RegexOptions.Compiled)]
    private static partial Regex BlockCommentPattern();

    // -----------------------------------------------------------------------
    // PHI-safety patterns
    // -----------------------------------------------------------------------

    // Unicode and regular string literals, including escaped quotes ('' inside)
    [GeneratedRegex(@"N?'(?:[^']|'')*'", RegexOptions.Compiled)]
    private static partial Regex StringLiteralPattern();

    // Hex literals (0xDEADBEEF) — can encode PHI, bypass string/numeric checks
    [GeneratedRegex(@"\b0x[0-9a-fA-F]+\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex HexLiteralPattern();

    // Numeric literal not preceded by @: bare numbers that could be MRNs/Rx numbers (5+ digits)
    [GeneratedRegex(@"(?<!@)\b(\d{5,})\b", RegexOptions.Compiled)]
    private static partial Regex NumericLiteralPattern();

    // Remote function check — anywhere in text
    [GeneratedRegex(@"\b(OPENQUERY|OPENROWSET|OPENDATASOURCE)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex RemoteFunctionPattern();

    // -----------------------------------------------------------------------
    // Table reference patterns
    // -----------------------------------------------------------------------

    // 3-part: db.schema.table — we capture schema.table (groups 2,3)
    // 2-part: schema.table — groups 1,2 (group 3 empty)
    // 1-part: table — group 1 only
    // Handles optional [] brackets. LIKE keyword excluded by post-filter.
    [GeneratedRegex(
        @"(?:FROM|JOIN|INTO|UPDATE)\s+(?:\[?(\w+)\]?\.)?(?:\[?(\w+)\]?\.)?(?:\[?(\w+)\]?)(?:\s|$|,|\()",
        RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex TableRefPattern();

    // Parameter names: @p1, @paramName, etc.
    [GeneratedRegex(@"@\w+", RegexOptions.Compiled)]
    private static partial Regex ParameterPattern();

    // CTE names: WITH name AS ( ... )
    [GeneratedRegex(@"\bWITH\s+(\w+)\s+AS\s*\(", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex CteNamePattern();

    // UNION/INTERSECT/EXCEPT separators — used to identify multi-branch queries
    [GeneratedRegex(@"\b(UNION|INTERSECT|EXCEPT)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex SetOperatorPattern();

    // -----------------------------------------------------------------------
    // Keywords to filter from table ref matches (not real table names)
    // -----------------------------------------------------------------------
    private static readonly HashSet<string> NonTableKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "LIKE", "SELECT", "WHERE", "SET", "ON", "AND", "OR", "NOT", "NULL",
        "WITH", "AS", "INNER", "LEFT", "RIGHT", "FULL", "OUTER", "CROSS",
        "TOP", "DISTINCT", "ALL", "BY", "GROUP", "HAVING", "ORDER", "ASC", "DESC"
    };

    // Maximum subquery recursion depth
    private const int MaxSubqueryDepth = 4;

    // -----------------------------------------------------------------------
    // Public API
    // -----------------------------------------------------------------------

    public static NormalizedQuery? TryNormalize(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql)) return null;

        // Step 1: Strip comments FIRST — comments can hide PHI or bypass keyword checks
        var stripped = StripComments(sql.Trim());

        // Step 2: Extract first keyword (after comment stripping)
        var firstWord = stripped.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries)[0]
            .TrimStart('(');

        // Step 3: Block DDL/EXEC/remote
        if (BlockedKeywords.Contains(firstWord)) return null;

        // Step 4: Only allow known DML; WITH is allowed only as a CTE prefix
        // For CTE queries, resolve the actual DML keyword after the CTE declaration
        string dmlKeyword = firstWord;
        if (firstWord.Equals("WITH", StringComparison.OrdinalIgnoreCase))
        {
            // Find the keyword that follows the CTE body — walk past WITH name AS (...)
            dmlKeyword = ResolveCteFirstKeyword(stripped) ?? "";
        }

        if (!AllowedKeywords.Contains(dmlKeyword)) return null;

        // Step 5: Block remote functions anywhere in the query (can appear mid-query as calls)
        if (RemoteFunctionPattern().IsMatch(stripped)) return null;

        // Step 6: Reject hex literals (PHI bypass path)
        if (HexLiteralPattern().IsMatch(stripped)) return null;

        // Step 7: Reject string literals (Unicode N'...' and regular '...')
        if (StringLiteralPattern().IsMatch(stripped)) return null;

        // Step 8: Reject bare numeric literals (MRN/Rx bypass path)
        if (NumericLiteralPattern().IsMatch(stripped)) return null;

        // Step 9: Collect CTE names so we don't count them as table references
        var cteNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in CteNamePattern().Matches(stripped))
            cteNames.Add(m.Groups[1].Value);

        // Step 10: Extract table references from all query branches
        var tables = ExtractTables(stripped, cteNames, 0);

        if (tables.Count == 0) return null;

        // Step 11: Build normalized shape — replace all parameter names with @p
        var shape = ParameterPattern().Replace(stripped, "@p");
        var isWrite = dmlKeyword.Equals("INSERT", StringComparison.OrdinalIgnoreCase)
                   || dmlKeyword.Equals("UPDATE", StringComparison.OrdinalIgnoreCase)
                   || dmlKeyword.Equals("DELETE", StringComparison.OrdinalIgnoreCase);

        return new NormalizedQuery(shape, tables, isWrite);
    }

    // -----------------------------------------------------------------------
    // Private helpers
    // -----------------------------------------------------------------------

    private static string StripComments(string sql)
    {
        // Block comments first (can span lines), then line comments
        var noBlock = BlockCommentPattern().Replace(sql, " ");
        return LineCommentPattern().Replace(noBlock, " ");
    }

    /// <summary>
    /// Extracts table references from the query text, recursing into subqueries
    /// up to MaxSubqueryDepth levels.
    /// </summary>
    private static IReadOnlyList<string> ExtractTables(string sql, HashSet<string> cteNames, int depth)
    {
        var tables = new List<string>();

        foreach (Match m in TableRefPattern().Matches(sql))
        {
            // Groups: 1=first part, 2=second part, 3=third part
            // For 3-part (db.schema.table): g1=db, g2=schema, g3=table → use schema.table
            // For 2-part (schema.table):    g1=schema, g2=empty, g3=table → use schema.table
            // For 1-part (table):           g1=empty, g2=empty, g3=table → use table
            string tableName;
            if (m.Groups[1].Success && m.Groups[2].Success && m.Groups[3].Success
                && !string.IsNullOrEmpty(m.Groups[1].Value)
                && !string.IsNullOrEmpty(m.Groups[2].Value)
                && !string.IsNullOrEmpty(m.Groups[3].Value))
            {
                // Three-part: skip db prefix, return schema.table
                tableName = $"{m.Groups[2].Value}.{m.Groups[3].Value}";
            }
            else if (m.Groups[1].Success && !string.IsNullOrEmpty(m.Groups[1].Value)
                     && m.Groups[3].Success && !string.IsNullOrEmpty(m.Groups[3].Value)
                     && (m.Groups[2].Value == string.Empty || !m.Groups[2].Success))
            {
                // Two-part: schema.table
                tableName = $"{m.Groups[1].Value}.{m.Groups[3].Value}";
            }
            else
            {
                // One-part: just the table name (group 3 is the innermost capture)
                var raw = m.Groups[3].Success && !string.IsNullOrEmpty(m.Groups[3].Value)
                    ? m.Groups[3].Value
                    : m.Groups[1].Value;
                tableName = raw;
            }

            // Filter pseudo-keywords and CTE names
            if (NonTableKeywords.Contains(tableName)) continue;
            if (cteNames.Contains(tableName)) continue;

            if (!tables.Contains(tableName, StringComparer.OrdinalIgnoreCase))
                tables.Add(tableName);
        }

        // Recurse into subqueries if depth allows
        if (depth < MaxSubqueryDepth)
        {
            foreach (var subSql in ExtractSubqueries(sql))
            {
                foreach (var t in ExtractTables(subSql, cteNames, depth + 1))
                {
                    if (!tables.Contains(t, StringComparer.OrdinalIgnoreCase))
                        tables.Add(t);
                }
            }
        }

        return tables;
    }

    /// <summary>
    /// Finds parenthesized subquery bodies that start with SELECT.
    /// Uses a simple bracket-depth scanner (no regex) to handle nesting correctly.
    /// </summary>
    private static IEnumerable<string> ExtractSubqueries(string sql)
    {
        var results = new List<string>();
        int i = 0;
        while (i < sql.Length)
        {
            if (sql[i] == '(')
            {
                // Scan to find the matching close paren
                int depth = 1;
                int start = i + 1;
                int j = start;
                while (j < sql.Length && depth > 0)
                {
                    if (sql[j] == '(') depth++;
                    else if (sql[j] == ')') depth--;
                    j++;
                }
                if (depth == 0)
                {
                    var inner = sql.Substring(start, j - start - 1).Trim();
                    // Only treat as subquery if it starts with SELECT (after trimming)
                    var innerFirst = inner.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries)
                        .FirstOrDefault() ?? "";
                    if (innerFirst.Equals("SELECT", StringComparison.OrdinalIgnoreCase))
                        results.Add(inner);
                    i = j;
                    continue;
                }
            }
            i++;
        }
        return results;
    }

    /// <summary>
    /// For a CTE query starting with WITH, finds the DML keyword that follows the
    /// last CTE declaration body. Returns null if not found or not a known DML keyword.
    /// Handles: WITH cte1 AS (...), cte2 AS (...) SELECT/INSERT/UPDATE/DELETE ...
    /// </summary>
    private static string? ResolveCteFirstKeyword(string sql)
    {
        // Walk past all "name AS (...)" blocks to find the trailing DML keyword
        int i = 0;
        // Skip the WITH keyword
        while (i < sql.Length && !char.IsWhiteSpace(sql[i])) i++;
        while (i < sql.Length && char.IsWhiteSpace(sql[i])) i++;

        while (i < sql.Length)
        {
            // Skip CTE name (identifier)
            while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] == '_')) i++;
            // Skip whitespace
            while (i < sql.Length && char.IsWhiteSpace(sql[i])) i++;
            // Expect AS
            if (i + 2 <= sql.Length && sql.Substring(i, 2).Equals("AS", StringComparison.OrdinalIgnoreCase))
            {
                i += 2;
                while (i < sql.Length && char.IsWhiteSpace(sql[i])) i++;
            }
            else
            {
                break;
            }
            // Expect ( — find matching )
            if (i < sql.Length && sql[i] == '(')
            {
                int depth = 1;
                i++;
                while (i < sql.Length && depth > 0)
                {
                    if (sql[i] == '(') depth++;
                    else if (sql[i] == ')') depth--;
                    i++;
                }
            }
            else
            {
                break;
            }
            // Skip whitespace and optional comma (multiple CTEs)
            while (i < sql.Length && char.IsWhiteSpace(sql[i])) i++;
            if (i < sql.Length && sql[i] == ',')
            {
                i++; // comma between CTEs, loop again
                while (i < sql.Length && char.IsWhiteSpace(sql[i])) i++;
                continue;
            }
            // What's here should be the DML keyword
            break;
        }

        if (i >= sql.Length) return null;

        // Extract next word
        int wordStart = i;
        while (i < sql.Length && !char.IsWhiteSpace(sql[i]) && sql[i] != '(') i++;
        return sql.Substring(wordStart, i - wordStart);
    }
}
