using System.Collections.Frozen;
using Serilog.Core;
using Serilog.Events;

namespace SuavoAgent.Core.Diagnostics;

/// <summary>
/// Final privacy boundary for Core's local console and rolling-file diagnostics.
/// Message templates are fixed source text; every structured property is reduced
/// to a safe structural value before any sink can render it.
/// </summary>
internal sealed class CoreDiagnosticRedactionEnricher : ILogEventEnricher
{
    internal const string RedactedValue = "redacted";

    private static readonly FrozenSet<string> AggregateNames =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Attempt", "Attempts", "Blocked", "Columns", "Completed", "Count",
            "Cycles", "Failed", "Files", "Invalid", "Items", "N", "Pending",
            "Remaining", "Retries", "Rows", "Scopes", "Skipped", "Skills",
            "Steps", "Total", "Warnings",
        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> BooleanNames =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Applied", "Available", "CanAct", "Canary", "CloudEnabled",
            "Connected", "DryRun", "Enabled", "Found", "HasKey", "Healthy", "IsReady",
            "Lossless", "Paused", "Ready", "Scrubbed", "Stopped", "Trusted",
            "Valid", "Verified",
        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        ArgumentNullException.ThrowIfNull(propertyFactory);

        foreach (var (name, value) in logEvent.Properties.ToArray())
        {
            var safe = SafeValue(name, value);
            if (!ReferenceEquals(safe, value))
                logEvent.AddOrUpdateProperty(new LogEventProperty(name, safe));
        }
    }

    private static LogEventPropertyValue SafeValue(string name, LogEventPropertyValue value)
    {
        if (value is ScalarValue { Value: bool } && BooleanNames.Contains(name))
            return value;

        if (value is ScalarValue scalar
            && AggregateNames.Contains(name)
            && IsNonNegativeIntegral(scalar.Value))
        {
            return value;
        }

        if (value is ScalarValue { Value: { } enumValue }
            && enumValue.GetType().IsEnum)
        {
            return new ScalarValue(enumValue.ToString());
        }

        if (value is ScalarValue { Value: string text })
        {
            if (name.Equals("EventCode", StringComparison.OrdinalIgnoreCase)
                && IsSafeEventCode(text))
            {
                return value;
            }

            if (name.Equals("ExceptionType", StringComparison.OrdinalIgnoreCase)
                && IsSafeExceptionType(text))
            {
                return value;
            }
        }

        return new ScalarValue(RedactedValue);
    }

    private static bool IsNonNegativeIntegral(object? value) => value switch
    {
        byte => true,
        ushort => true,
        uint => true,
        ulong => true,
        sbyte number => number >= 0,
        short number => number >= 0,
        int number => number >= 0,
        long number => number >= 0,
        _ => false,
    };

    private static bool IsSafeEventCode(string value) =>
        value.Length is > 5 and <= 96
        && value.StartsWith("core.", StringComparison.Ordinal)
        && value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_');

    private static bool IsSafeExceptionType(string value) =>
        value.Length is > 0 and <= 96
        && value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '_' or '.');
}
