using System.Text.Json;
using System.Text.Json.Serialization;

namespace SuavoAgent.Core.Config;

public sealed class IndustryAdapter
{
    [JsonPropertyName("industry")]
    public string Industry { get; set; } = "unknown";

    [JsonPropertyName("primary_apps")]
    public List<string> PrimaryApps { get; set; } = new();

    [JsonPropertyName("compliance")]
    public List<string> Compliance { get; set; } = new();

    [JsonPropertyName("known_domains")]
    public Dictionary<string, List<string>> KnownDomains { get; set; } = new();

    [JsonPropertyName("document_categories")]
    public Dictionary<string, DocumentCategoryPattern> DocumentCategories { get; set; } = new();

    [JsonPropertyName("phi_column_patterns")]
    public List<string> PhiColumnPatterns { get; set; } = new();

    public string? ClassifyDomain(string domain)
    {
        var lower = domain.ToLowerInvariant();
        foreach (var (category, domains) in KnownDomains)
        {
            if (domains.Any(d => lower.Contains(d.ToLowerInvariant())))
                return category;
        }
        return null;
    }

    public bool IsPrimaryApp(string processName) =>
        PrimaryApps.Any(p => p.Equals(processName, StringComparison.OrdinalIgnoreCase));

    public static IndustryAdapter LoadFromFile(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<IndustryAdapter>(json) ?? new IndustryAdapter();
    }

    public static IndustryAdapter LoadForIndustry(string industry, string? adaptersDir = null)
    {
        adaptersDir ??= Path.Combine(AppContext.BaseDirectory, "adapters");
        var filePath = Path.Combine(adaptersDir, $"{industry.ToLowerInvariant()}.json");
        return File.Exists(filePath) ? LoadFromFile(filePath) : new IndustryAdapter { Industry = industry };
    }
}

public sealed class DocumentCategoryPattern
{
    [JsonPropertyName("column_patterns")]
    public List<string> ColumnPatterns { get; set; } = new();
}
