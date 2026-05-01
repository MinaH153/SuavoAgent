using System.Text.Json;
using System.Text.RegularExpressions;

namespace SuavoAgent.Core.Cloud;

internal static partial class CloudErrorSanitizer
{
    private static readonly HashSet<string> KnownHumanSafeReasons = new(StringComparer.OrdinalIgnoreCase)
    {
        "Agent not found",
        "Agent paused",
        "Invalid signature",
        "Invalid timestamp",
        "Missing agent auth headers",
        "Timestamp outside allowed window",
        "Token not found",
        "Token expired",
        "Token already used",
    };

    public static string FromBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return "empty_error_body";

        var candidate = ExtractErrorField(body) ?? body;
        candidate = candidate.Replace("\r", " ").Replace("\n", " ").Trim();
        if (candidate.Length > 240)
            candidate = candidate[..240];

        return IsKnownSafeReason(candidate)
            ? candidate
            : "redacted_error_body";
    }

    private static bool IsKnownSafeReason(string candidate)
    {
        if (KnownHumanSafeReasons.Contains(candidate))
            return true;

        // Permit compact machine-readable reasons only. Free-form prose with
        // spaces can accidentally contain patient names or addresses.
        return SafeCodePattern().IsMatch(candidate);
    }

    private static string? ExtractErrorField(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return null;
            if (doc.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.String)
            {
                return error.GetString();
            }
            if (doc.RootElement.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String)
            {
                return message.GetString();
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    [GeneratedRegex(@"^[A-Za-z0-9_.:-]{1,96}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeCodePattern();
}
