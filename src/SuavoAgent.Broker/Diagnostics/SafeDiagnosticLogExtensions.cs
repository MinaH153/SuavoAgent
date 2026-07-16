using System.Runtime.CompilerServices;

namespace Microsoft.Extensions.Logging;

/// <summary>PHI-safe caught-exception logging boundary for Broker.</summary>
internal static class SafeDiagnosticLogExtensions
{
    public static void LogSafeDebug(
        this ILogger logger,
        Exception exception,
        [CallerMemberName] string operation = "unknown") =>
        Write(logger, LogLevel.Debug, exception, operation);

    public static void LogSafeWarning(
        this ILogger logger,
        Exception exception,
        [CallerMemberName] string operation = "unknown") =>
        Write(logger, LogLevel.Warning, exception, operation);

    public static void LogSafeError(
        this ILogger logger,
        Exception exception,
        [CallerMemberName] string operation = "unknown") =>
        Write(logger, LogLevel.Error, exception, operation);

    private static void Write(ILogger logger, LogLevel level, Exception exception, string operation)
    {
        logger.Log(
            level,
            "{EventCode} exception_type={ExceptionType}",
            $"broker.{NormalizeIdentifier(operation)}.exception",
            NormalizeIdentifier(exception.GetType().Name));
    }

    private static string NormalizeIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "unknown";

        Span<char> buffer = stackalloc char[Math.Min(value.Length, 96)];
        var written = 0;
        foreach (var ch in value)
        {
            if (written == buffer.Length)
                break;

            buffer[written++] = char.IsAsciiLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '_';
        }

        return written == 0 ? "unknown" : new string(buffer[..written]);
    }
}
