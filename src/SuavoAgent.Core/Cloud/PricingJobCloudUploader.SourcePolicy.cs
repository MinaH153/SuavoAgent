namespace SuavoAgent.Core.Cloud;

public sealed partial class PricingJobCloudUploader
{
    private static string SafeSource(string? value)
    {
        return value is "sql" or "uia" or "vision"
            ? value
            : throw new InvalidOperationException("pricing_result_source_invalid");
    }

    private static string SourceFromExecutionMode(string? value) => value switch
    {
        "SqlFirst" => "sql",
        "UiaFirst" => "uia",
        "VisionFirst" => "vision",
        _ => SafeSource(value),
    };
}
