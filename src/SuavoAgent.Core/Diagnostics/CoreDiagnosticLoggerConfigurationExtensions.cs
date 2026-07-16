using Serilog;

namespace SuavoAgent.Core.Diagnostics;

internal static class CoreDiagnosticLoggerConfigurationExtensions
{
    /// <summary>Installs the mandatory last-mile privacy boundary for every Core sink.</summary>
    public static LoggerConfiguration SanitizeCoreDiagnostics(
        this LoggerConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return configuration.Enrich.With<CoreDiagnosticRedactionEnricher>();
    }
}
