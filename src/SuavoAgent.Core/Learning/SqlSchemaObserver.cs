namespace SuavoAgent.Core.Learning;

/// <summary>
/// SQL Server schema and query observer. Discovers INFORMATION_SCHEMA and DMV patterns.
/// STUB: Full implementation in Task 6.
/// </summary>
public static class SqlSchemaObserver
{
    public static string InferColumnPurpose(string columnName)
    {
        var lower = columnName.ToLowerInvariant();
        if (lower.EndsWith("id")) return "identifier";
        if (lower.Contains("date") || lower.Contains("created") || lower.Contains("updated")
            || lower.Contains("time") || lower.StartsWith("dt")) return "temporal";
        if (lower.Contains("npi") || lower.Contains("dea") || lower.Contains("license")) return "regulatory";
        if (lower.Contains("price") || lower.Contains("amount") || lower.Contains("cost")
            || lower.Contains("quantity") || lower.Contains("qty")) return "amount";
        if (lower.Contains("name") || lower.Contains("first") || lower.Contains("last")) return "name";
        return "unknown";
    }

    public static bool IsLikelyForeignKey(string columnName)
    {
        var lower = columnName.ToLowerInvariant();
        return lower.EndsWith("id") && lower != "id";
    }
}
