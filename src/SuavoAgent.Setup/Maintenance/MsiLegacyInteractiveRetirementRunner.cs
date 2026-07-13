namespace SuavoAgent.Setup.Maintenance;

internal enum MsiLegacyInteractiveRetirementExitCode
{
    Success = 0,
    InvalidArguments = 50,
    UnsupportedHost = 51,
    CleanupFailed = 52,
}

/// <summary>
/// Fixed MSI commit action that retires the exact former interactive Broker
/// launch. Commit scheduling means no shortcut or process is touched when MSI
/// rolls back, and this path never depends on device pairing being launched.
/// </summary>
internal static class MsiLegacyInteractiveRetirementRunner
{
    internal const string Switch = "--msi-retire-legacy-interactive";

    internal static bool IsRequested(IReadOnlyList<string>? arguments) =>
        arguments?.Any(argument => string.Equals(
            argument,
            Switch,
            StringComparison.OrdinalIgnoreCase)) == true;

    internal static int Run(IReadOnlyList<string>? arguments) =>
        Run(
            arguments,
            OperatingSystem.IsWindows(),
            LegacyInteractiveLaunchRetirement.Execute);

    internal static int Run(
        IReadOnlyList<string>? arguments,
        bool isWindows,
        Func<LegacyInteractiveLaunchCleanupResult> cleanup)
    {
        if (arguments is null ||
            arguments.Count != 1 ||
            !string.Equals(
                arguments[0],
                Switch,
                StringComparison.OrdinalIgnoreCase))
            return (int)MsiLegacyInteractiveRetirementExitCode.InvalidArguments;
        if (!isWindows)
            return (int)MsiLegacyInteractiveRetirementExitCode.UnsupportedHost;

        ArgumentNullException.ThrowIfNull(cleanup);
        try
        {
            var result = cleanup();
            return result.Succeeded && !result.RunnableLegacyPathRemains
                ? (int)MsiLegacyInteractiveRetirementExitCode.Success
                : (int)MsiLegacyInteractiveRetirementExitCode.CleanupFailed;
        }
        catch
        {
            // MSI receives a bounded code only. Shortcut targets, user names,
            // paths, process arguments, and any other workstation data stay out
            // of the Windows Installer log.
            return (int)MsiLegacyInteractiveRetirementExitCode.CleanupFailed;
        }
    }
}
