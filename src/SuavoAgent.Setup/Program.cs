using System.Runtime.InteropServices;
using Avalonia;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Diagnostics;
using SuavoAgent.Setup.Doctor;
using SuavoAgent.Setup.InstallerSupport;
using SuavoAgent.Setup.Maintenance;

namespace SuavoAgent.Setup;

internal static class Program
{
    internal const string UninstallUiSwitch = "--uninstall-ui";
    internal const string RepairUiSwitch = "--repair-ui";
    internal const string ConnectInstalledSwitch = "--connect-installed";

    [STAThread]
    public static int Main(string[] args)
    {
        // Diagnostic Mesh: Wire.AttachUnhandledHooks MUST be the literal
        // first executable statement of Main (spec §7 PR 4 wire-ordering
        // invariant; verified by WireOrderingTests). Bug 24's CLR fast-
        // fail surface lives in this entry point's BuildAvaloniaApp call.
        Wire.AttachUnhandledHooks(WireComponent.Setup, new WireOptions
        {
            LocalCrashLogPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "SuavoAgent", "logs", "setup-crash.log"),
            LocalJournalPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "SuavoAgent", "diagnostics", "events.jsonl"),
            Dsn = Environment.GetEnvironmentVariable("SUAVO_SENTRY_DSN"),
            EnableSentry = true,
        });

        // MSI invokes fixed, non-sensitive apply/rollback/commit maintenance modes
        // as elevated in-script actions around service startup. Route any occurrence
        // to the strict parser so malformed invocations fail closed instead of
        // falling through to the pairing UI.
        if (MsiServiceHardeningRunner.IsRequested(args))
            return MsiServiceHardeningRunner.Run(args);

        // Successful MSI commit retires the exact former developer-publish
        // Broker launch even if the operator never opens device pairing.
        if (MsiLegacyInteractiveRetirementRunner.IsRequested(args))
            return MsiLegacyInteractiveRetirementRunner.Run(args);

        if (IsDoctorMode(args))
        {
            AttachParentConsole();
            return DoctorRunner.RunAsync(args, System.Threading.CancellationToken.None).GetAwaiter().GetResult();
        }

        if (IsUpdateRunnerMode(args))
        {
            AttachParentConsole();
            return NativeOtaActivationCoordinator.RunRunner(args);
        }

        if (IsResumeUpdateMode(args))
        {
            AttachParentConsole();
            return NativeOtaActivationCoordinator.RunResume(args);
        }

        if (IsActivateUpdateMode(args))
        {
            AttachParentConsole();
            return NativeOtaActivationCoordinator.RunInitial(args);
        }

        if (IsRepairServicesMode(args))
        {
            AttachParentConsole();
            return NativeRepairInstaller.Run(args);
        }

        if (IsPioneerRxApprovalInstallMode(args))
        {
            AttachParentConsole();
            return PioneerRxApprovalInstallCoordinator.Run(args);
        }

        if (IsPioneerRxApprovalBootstrapMode(args))
        {
            AttachParentConsole();
            return PioneerRxApprovalBootstrapCoordinator.Run(args);
        }

        if (IsUninstallMode(args))
        {
            AttachParentConsole();
            return UninstallInstaller.RunAsync(args).GetAwaiter().GetResult();
        }

        if (IsUninstallUiMode(args))
        {
            // The registered maintenance host lives inside the directory that
            // the GUI removes. Hand off to a native temp copy before Avalonia
            // starts so the visible uninstall can still prove zero residue.
            try
            {
                if (UninstallInstaller.TryReExecFromTemp(args))
                    return 0;
            }
            catch
            {
                return 1;
            }
            UninstallInstaller.ScheduleCurrentTempCleanup(args);
        }

        // A prior offline self-uninstall may have removed every runtime binary after
        // durably signing its completion ticket. Replay that exact no-HMAC ticket
        // before a replacement device code can create new authority on this machine.
        // This is a pairing authority gate, not an uninstall gate. Windows
        // Settings must always be able to open local removal even while an
        // older cloud finalization receipt is awaiting network recovery.
        if (!IsUninstallUiMode(args) && !IsRepairUiMode(args))
        {
            var pendingUninstall = SelfUninstallCompletionFinalizer
                .ReplayProductionBeforePairingAsync(CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            if (!pendingUninstall.IsFinalized)
            {
                AttachParentConsole();
                ConsoleUI.WriteWarn(
                    $"A previous uninstall is still awaiting secure cloud finalization " +
                    $"({pendingUninstall.Code}). Connect this PC to the internet and run Setup again.");
                return 3;
            }
        }

        // Wrap BuildAvaloniaApp + Lifetime in try/catch so XAML compile
        // failures during AppBuilder.Configure (Bug 24's class) reach Wire
        // before the CLR fast-fails on the unhandled exception.
        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Wire.ReportException(WireComponent.Setup, ex, stage: "AvaloniaConfigure");
            throw;
        }
    }

    // Public so Avalonia's previewer and designer tooling can discover it.
    // .AfterSetup hook installs the Avalonia dispatcher exception capture
    // so UI-thread exceptions route through Wire before the dispatcher's
    // default unhandled path runs (Mesh PR 4d).
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<Gui.App>()
            .UsePlatformDetect()
            .LogToTrace()
            .AfterSetup(_ => SuavoAgent.Setup.Diagnostics.AvaloniaDispatcherHook.Install());

    // Uninstall is its own headless mode (no onboarding credentials needed):
    // SuavoSetup.exe --uninstall  (the GUI Welcome screen also routes here).
    private static bool IsUninstallMode(string[] args) =>
        args.Any(a => string.Equals(a, "--uninstall", StringComparison.OrdinalIgnoreCase));

    internal static bool IsUninstallUiMode(IReadOnlyList<string>? args) =>
        args?.Any(a => string.Equals(
            a,
            UninstallUiSwitch,
            StringComparison.OrdinalIgnoreCase)) == true;

    internal static bool IsRepairUiMode(IReadOnlyList<string>? args) =>
        args?.Any(a => string.Equals(
            a,
            RepairUiSwitch,
            StringComparison.OrdinalIgnoreCase)) == true;

    internal static bool IsConnectInstalledMode(IReadOnlyList<string>? args) =>
        args?.Any(a => string.Equals(
            a,
            ConnectInstalledSwitch,
            StringComparison.OrdinalIgnoreCase)) == true;

    // Doctor mode: read-only health layer-trace, exits 0 (healthy) or 1 (degraded).
    // Made internal so DoctorModeRoutingTests (via InternalsVisibleTo) can assert it.
    internal static bool IsDoctorMode(string[] args) =>
        args.Any(a => string.Equals(a, "--doctor", StringComparison.OrdinalIgnoreCase));

    // Native maintenance mode. This must route before console/GUI setup so a LocalSystem
    // Watchdog or Broker can repair the installed service cohort without loading Avalonia,
    // consuming a device code, or reaching the registration API.
    internal static bool IsRepairServicesMode(string[] args) =>
        args.Any(a => string.Equals(
            a,
            MaintenanceContract.RepairServicesSwitch,
            StringComparison.OrdinalIgnoreCase));

    internal static bool IsPioneerRxApprovalInstallMode(string[] args) =>
        args.Any(a => string.Equals(
            a,
            PioneerRxApprovalMaintenanceContract.InstallSwitch,
            StringComparison.OrdinalIgnoreCase));

    internal static bool IsPioneerRxApprovalBootstrapMode(string[] args) =>
        args.Any(a => string.Equals(
            a,
            PioneerRxApprovalBootstrapContract.BootstrapSwitch,
            StringComparison.OrdinalIgnoreCase));

    internal static bool IsActivateUpdateMode(string[] args) =>
        args.Any(a => string.Equals(
            a,
            UpdateActivationContract.ActivateSwitch,
            StringComparison.OrdinalIgnoreCase));

    internal static bool IsUpdateRunnerMode(string[] args) =>
        args.Any(a => string.Equals(
            a,
            UpdateActivationContract.RunnerSwitch,
            StringComparison.OrdinalIgnoreCase));

    internal static bool IsResumeUpdateMode(string[] args) =>
        args.Any(a => string.Equals(
            a,
            UpdateActivationContract.ResumeSwitch,
            StringComparison.OrdinalIgnoreCase));

    // Reattach to the parent terminal for headless maintenance and deployment output.
    // Lets fleet-deploy scripts still see phase output from a WinExe binary.
    private const int ATTACH_PARENT_PROCESS = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int dwProcessId);

    private static void AttachParentConsole()
    {
        try { AttachConsole(ATTACH_PARENT_PROCESS); }
        catch { /* No parent console available — GUI mode will still work. */ }
    }
}
