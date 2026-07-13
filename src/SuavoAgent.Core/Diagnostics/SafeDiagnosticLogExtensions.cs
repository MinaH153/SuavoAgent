using System.Runtime.CompilerServices;

namespace Microsoft.Extensions.Logging;

/// <summary>
/// Keeps caught exceptions out of every diagnostics sink. Only a stable
/// component/operation code and the CLR exception type are emitted; messages,
/// stack traces, paths, identifiers, and exception data never cross the log boundary.
/// </summary>
internal static class SafeDiagnosticLogExtensions
{
    private const string Component = "core";

    public static void LogSafeDebug(
        this ILogger logger,
        Exception exception,
        [CallerMemberName] string operation = "unknown") =>
        logger.LogDebug(
            "{EventCode} exception_type={ExceptionType}",
            EventCode(operation),
            ExceptionType(exception));

    public static void LogSafeInformation(
        this ILogger logger,
        Exception exception,
        [CallerMemberName] string operation = "unknown") =>
        logger.LogInformation(
            "{EventCode} exception_type={ExceptionType}",
            EventCode(operation),
            ExceptionType(exception));

    public static void LogSafeWarning(
        this ILogger logger,
        Exception exception,
        [CallerMemberName] string operation = "unknown") =>
        logger.LogWarning(
            "{EventCode} exception_type={ExceptionType}",
            EventCode(operation),
            ExceptionType(exception));

    public static void LogSafeError(
        this ILogger logger,
        Exception exception,
        [CallerMemberName] string operation = "unknown") =>
        logger.LogError(
            "{EventCode} exception_type={ExceptionType}",
            EventCode(operation),
            ExceptionType(exception));

    public static void LogSafeCritical(
        this ILogger logger,
        Exception exception,
        [CallerMemberName] string operation = "unknown") =>
        logger.LogCritical(
            "{EventCode} exception_type={ExceptionType}",
            EventCode(operation),
            ExceptionType(exception));

    private static string EventCode(string operation) =>
        $"{Component}.{NormalizeIdentifier(operation)}.exception";

    private static string ExceptionType(Exception exception) =>
        NormalizeIdentifier(exception.GetType().Name);

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
