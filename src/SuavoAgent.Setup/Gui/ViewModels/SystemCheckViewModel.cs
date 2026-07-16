using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Setup.Gui.Services;
using SuavoAgent.Setup.Preflight;

namespace SuavoAgent.Setup.Gui.ViewModels;

public sealed class CheckItem : ViewModelBase
{
    private CheckState _state = CheckState.Pending;
    private string _detail = "Checking…";

    public CheckItem(string title, CheckTier tier)
    {
        Title = title;
        Tier = tier;
    }

    public string Title { get; }

    /// <summary>Whether this probe can block the install, or only informs it.</summary>
    public CheckTier Tier { get; }

    public CheckState State
    {
        get => _state;
        set
        {
            SetField(ref _state, value);
            RaisePropertyChanged(nameof(Icon));
            RaisePropertyChanged(nameof(ColorHex));
            RaisePropertyChanged(nameof(StatusLabel));
        }
    }

    public string Detail
    {
        get => _detail;
        set => SetField(ref _detail, value);
    }

    public string Icon => _state switch
    {
        CheckState.Ok => "✓",
        CheckState.Warn => "!",
        CheckState.Fail => "✕",
        CheckState.Deferred => "↻",
        _ => "•",
    };

    public string ColorHex => _state switch
    {
        CheckState.Ok => "#7A9B6E",        // sage — ready
        CheckState.Warn => "#EA580C",      // warning orange — attention only
        CheckState.Fail => "#C95454",      // wine — blocks
        CheckState.Deferred => "#2563EB",  // sapphire — self-configures
        _ => "#6E6A62",                    // subtle — still scanning
    };

    /// <summary>Right-aligned pill text — the human verdict for this row.</summary>
    public string StatusLabel => _state switch
    {
        CheckState.Ok => "Ready",
        CheckState.Warn => Tier == CheckTier.Required ? "Required" : "Recommended",
        CheckState.Fail => "Required",
        CheckState.Deferred => Tier == CheckTier.Required ? "Required" : "Self-configures",
        _ => "Checking…",
    };
}

public enum CheckState { Pending, Ok, Warn, Fail, Deferred }

/// <summary>
/// Required checks can block the install. Informational checks only annotate it —
/// their failure becomes a warning or a deferred (self-healing) item, never a wall.
/// </summary>
public enum CheckTier { Required, Informational }

internal sealed class SystemCheckViewModel : ViewModelBase
{
    private static readonly HttpClient SharedHttp = new();

    private readonly InstallContext _ctx;
    private readonly Action _onReady;
    private readonly Func<bool> _probeIsWindows10;
    private readonly Func<PioneerRxDiscovery.DiscoveryResult?> _probePioneer;
    private readonly Func<string, SqlCredentialDiscovery.SqlCredentials?> _probeSql;
    private readonly Func<CancellationToken, Task<(CheckState, string)>> _probeRuntime;
    private readonly Func<(CheckState, string)> _probeDisk;
    private readonly Func<(CheckState, string)> _probeEncryptedStorage;
    private readonly Func<(CheckState, string)> _probeDeviceKey;
    private bool _isReady;

    // Required = the only thing the binaries truly need to land + run.
    public CheckItem OsCheck { get; } = new("Windows 11 24H2 or newer", CheckTier.Required);
    public CheckItem DiskCheck { get; } = new("Signed brain storage", CheckTier.Required);
    public CheckItem RuntimeCheck { get; } = new("Runtime components", CheckTier.Required);
    public CheckItem BitLockerCheck { get; } = new("Encrypted storage (BitLocker)", CheckTier.Required);
    public CheckItem DeviceKeyCheck { get; } = new("TPM-backed device identity", CheckTier.Required);
    // Pharmacy activation currently requires live PioneerRx + schema proof, so
    // preflight must tell the same truth as the post-install health milestone.
    public CheckItem PioneerCheck { get; } = new("PioneerRx installation", CheckTier.Required);
    public CheckItem SqlCheck { get; } = new("SQL Server credentials", CheckTier.Required);

    public ObservableCollection<CheckItem> Items { get; }

    public RelayCommand ContinueCommand { get; }

    public bool IsReady
    {
        get => _isReady;
        private set
        {
            if (SetField(ref _isReady, value))
                ContinueCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>One-line banner verdict that updates live as probes resolve.</summary>
    public string ReadinessHeadline => OsCheck.State switch
    {
        CheckState.Fail => "This computer can't run SuavoAgent",
        _ when !IsReady => "Checking this computer…",
        _ when _ctx.ConfigureInstalledCohort => "Ready to connect securely",
        _ => "Ready to install securely",
    };

    public string ReadinessDetail => OsCheck.State switch
    {
        CheckState.Fail => "Windows 11 24H2 or newer (64-bit) is required.",
        _ when !IsReady => "Disk, encryption, TPM identity, PioneerRx, and SQL must all be ready.",
        _ => "This workstation passed the pharmacy security and activation gates.",
    };

    public string ReadinessIcon => OsCheck.State switch
    {
        CheckState.Fail => "✕",
        _ when !IsReady => "↻",
        _ => "✓",
    };

    public string ReadinessColorHex => OsCheck.State switch
    {
        CheckState.Fail => "#C95454",
        _ when !IsReady => "#2563EB",
        _ => "#7A9B6E",
    };

    public SystemCheckViewModel(
        InstallContext ctx,
        Action onReady,
        Func<bool>? probeIsWindows10 = null,
        Func<PioneerRxDiscovery.DiscoveryResult?>? probePioneer = null,
        Func<string, SqlCredentialDiscovery.SqlCredentials?>? probeSql = null,
        Func<CancellationToken, Task<(CheckState, string)>>? probeRuntime = null,
        Func<(CheckState, string)>? probeDisk = null,
        Func<(CheckState, string)>? probeEncryptedStorage = null,
        Func<(CheckState, string)>? probeDeviceKey = null)
    {
        _ctx = ctx;
        _onReady = onReady;
        // Probes are injectable so the scan is unit-testable without a Windows box;
        // defaults are the real Win32/registry/SQL implementations.
        _probeIsWindows10 = probeIsWindows10 ??
            (() => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100));
        _probePioneer = probePioneer ?? PioneerRxDiscovery.Discover;
        _probeSql = probeSql ?? SqlCredentialDiscovery.TryAutoDiscover;
        _probeRuntime = probeRuntime ?? RealProbeRuntime;
        _probeDisk = probeDisk ?? ProbeDisk;
        _probeEncryptedStorage = probeEncryptedStorage ?? ProbeBitLocker;
        _probeDeviceKey = probeDeviceKey ?? ProbeDeviceAuthority;

        Items = new ObservableCollection<CheckItem>
        {
            OsCheck, DiskCheck, RuntimeCheck, BitLockerCheck, DeviceKeyCheck,
            PioneerCheck, SqlCheck,
        };

        // Readiness is a live function of the probe states — the Continue button
        // lights up the moment the required checks pass, regardless of the
        // self-healing ones.
        foreach (var item in Items)
            item.PropertyChanged += OnCheckChanged;

        ContinueCommand = new RelayCommand(_onReady, () => IsReady);
    }

    private void OnCheckChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CheckItem.State)) return;
        RecomputeReadiness();
        // The hero banner is derived from the check states (esp. OS), so refresh
        // it on every probe result — not only when IsReady flips.
        RaisePropertyChanged(nameof(ReadinessHeadline));
        RaisePropertyChanged(nameof(ReadinessDetail));
        RaisePropertyChanged(nameof(ReadinessIcon));
        RaisePropertyChanged(nameof(ReadinessColorHex));
    }

    /// <summary>
    /// HIPAA-first gate: every required control must return a positive Ok.
    /// Warn/Deferred are not authorization to install PHI-bearing software.
    /// </summary>
    private void RecomputeReadiness() =>
        IsReady = Items.All(i => i.State != CheckState.Pending)
                  && Items.Where(i => i.Tier == CheckTier.Required)
                          .All(i => i.State == CheckState.Ok);

    /// <summary>
    /// Runs every probe on a background thread (WMI, registry, SQL — all
    /// blocking I/O), then applies the results back on the UI thread. Avalonia
    /// only reliably reflects bound-property + command changes raised on the UI
    /// thread, so the probing and the view-model mutation are split deliberately.
    /// </summary>
    public Task RunChecksAsync() => Task.Run(() =>
    {
        var outcome = Probe();
        Dispatcher.UIThread.Post(() => Apply(outcome));
    });

    /// <summary>Pure, background-safe: runs all probes, never mutates the view-model.</summary>
    internal ProbeOutcome Probe()
    {
        var os = ProbeOs();
        var disk = _probeDisk();
        // ProbeRuntime may download+install the VC++ redistributable — it is async internally
        // but called with .GetAwaiter().GetResult() because Probe() is always invoked from a
        // background thread (inside Task.Run), never from the UI thread.
        var runtime = _probeRuntime(CancellationToken.None).GetAwaiter().GetResult();
        var bitLocker = _probeEncryptedStorage();
        var deviceKey = _probeDeviceKey();
        var (pioneerState, pioneerDetail, pioneer) = ProbePioneer();
        var (sqlState, sqlDetail, sqlCreds) = ProbeSql(pioneer);
        return new ProbeOutcome(
            os, disk, runtime, bitLocker, deviceKey,
            (pioneerState, pioneerDetail), (sqlState, sqlDetail),
            pioneer, sqlCreds);
    }

    /// <summary>UI-thread only: applies probe results to the bound view-model.</summary>
    internal void Apply(ProbeOutcome o)
    {
        (OsCheck.State, OsCheck.Detail) = o.Os;
        (DiskCheck.State, DiskCheck.Detail) = o.Disk;
        (RuntimeCheck.State, RuntimeCheck.Detail) = o.Runtime;
        (BitLockerCheck.State, BitLockerCheck.Detail) = o.BitLocker;
        (DeviceKeyCheck.State, DeviceKeyCheck.Detail) = o.DeviceKey;
        (PioneerCheck.State, PioneerCheck.Detail) = o.Pioneer;
        (SqlCheck.State, SqlCheck.Detail) = o.Sql;
        _ctx.Pioneer = o.PioneerResult;
        if (o.SqlCreds != null) _ctx.SqlCredentials = o.SqlCreds;
        RecomputeReadiness();
    }

    // ── Individual probes — each fully isolated so one failure can never strand
    //    the scan (the "if some fail, self-heal moves along" principle). ────────

    private (CheckState, string) ProbeOs()
    {
        try
        {
            return _probeIsWindows10()
                ? (CheckState.Ok, Environment.OSVersion.VersionString)
                : (CheckState.Fail, "Windows 11 24H2 or newer (64-bit) required.");
        }
        catch
        {
            // Can't confirm a compatible OS → block (the one hard requirement).
            return (CheckState.Fail, "Could not confirm Windows version.");
        }
    }

    private (CheckState, string) ProbeDisk()
    {
        var result = BrainDiskSpaceGate.Evaluate(
            _ctx.InstallDir,
            _ctx.DataDir,
            _ctx.Config.Reasoning,
            root => new DriveInfo(root).AvailableFreeSpace);
        return (result.IsSufficient ? CheckState.Ok : CheckState.Fail, result.Detail);
    }

    private (CheckState, string) ProbeBitLocker()
    {
        // state.db and local evidence can contain PHI; full-volume encryption is
        // therefore a hard pharmacy install gate until app-level DB encryption ships.
        try
        {
            var result = WindowsVolumeEncryptionProbe.Evaluate(
                new[]
                {
                    _ctx.DataDir,
                    ServiceInstaller.DefaultRetentionRoot(_ctx.DataDir),
                },
                WindowsVolumeEncryptionProbe.ProbeProduction);
            return (result.IsProtected ? CheckState.Ok : CheckState.Fail, result.Detail);
        }
        catch
        {
            return (CheckState.Fail,
                "Could not prove BitLocker protection on every PHI storage volume. Enable BitLocker, then retry.");
        }
    }

    private (CheckState, string) ProbeDeviceAuthority()
    {
        if (!OperatingSystem.IsWindows())
            return (CheckState.Fail,
                "A Windows TPM 2.0 device is required for the workstation identity key.");
        var config = _ctx.Config;
        return !string.IsNullOrWhiteSpace(config.DeviceKeyId) &&
               !string.IsNullOrWhiteSpace(config.DeviceKeyName) &&
               !string.IsNullOrWhiteSpace(config.DeviceFingerprint)
            ? (CheckState.Ok, "TPM-backed device key enrolled for this workstation")
            : (CheckState.Fail,
                "Secure device key is unavailable. Enable TPM 2.0 in firmware, clear any TPM error in Windows Security, then restart Setup.");
    }

    private (CheckState, string, PioneerRxDiscovery.DiscoveryResult?) ProbePioneer()
    {
        // Pharmacy activation is PioneerRx-specific today, so absence is a
        // required failure. Vertical-agnostic installs need a separate signed
        // product mode rather than silently weakening this pharmacy gate.
        try
        {
            var pioneer = _probePioneer();
            return pioneer != null
                ? (CheckState.Ok, pioneer.PioneerDir, pioneer)
                : (CheckState.Fail,
                   "PioneerRx is required for pharmacy activation. Install or start PioneerRx, then retry.",
                   (PioneerRxDiscovery.DiscoveryResult?)null);
        }
        catch
        {
            return (CheckState.Fail,
                "PioneerRx detection failed. Verify the installation and restart Setup.",
                null);
        }
    }

    private (CheckState, string, SqlCredentialDiscovery.SqlCredentials?) ProbeSql(
        PioneerRxDiscovery.DiscoveryResult? pioneer)
    {
        if (pioneer == null)
            return (CheckState.Fail, "SQL cannot be verified until PioneerRx is detected.", null);

        try
        {
            var creds = _probeSql(pioneer.PioneerConfig);
            return creds != null
                ? (CheckState.Ok,
                   $"{creds.Server} / {creds.Database} ({(creds.IsWindowsAuth ? "Windows" : $"SQL: {creds.User}")})",
                   creds)
                : (CheckState.Fail,
                   "SQL auto-discovery failed. Verify the PioneerRx database service and permissions, then retry.",
                   (SqlCredentialDiscovery.SqlCredentials?)null);
        }
        catch
        {
            return (CheckState.Fail,
                "SQL verification failed. Verify the PioneerRx database service and permissions, then retry.",
                null);
        }
    }

    /// <summary>
    /// Real runtime probe: checks for the VC++ 2015-2022 x64 redistributable;
    /// if absent, downloads and silently installs it. Runs on the background probe
    /// thread — never on the UI thread.
    /// </summary>
    private static async Task<(CheckState, string)> RealProbeRuntime(CancellationToken ct)
    {
        try
        {
            var status = new VcRedistChecker().Check();
            if (status.Installed)
                return (CheckState.Ok, "VC++ runtime present");

            var outcome = await new VcRedistPreflight(
                new VcRedistChecker(),
                () => new VcRedistProvider(SharedHttp, VcRedistPreflight.AssetUrl, VcRedistPreflight.Sha256),
                new VcRedistInstaller()
            ).EnsureAsync(ct);

            return outcome.State == VcRedistPreflightState.Failed
                ? (CheckState.Fail, outcome.Detail)
                : (CheckState.Ok, outcome.Detail);
        }
        catch (Exception)
        {
            return (CheckState.Fail,
                "Runtime check failed. Install vc_redist.x64.exe from Microsoft, then retry. Support code: SETUP-RUNTIME-CHECK");
        }
    }
}

/// <summary>Immutable result of one background scan — applied to the VM on the UI thread.</summary>
internal sealed record ProbeOutcome(
    (CheckState State, string Detail) Os,
    (CheckState State, string Detail) Disk,
    (CheckState State, string Detail) Runtime,
    (CheckState State, string Detail) BitLocker,
    (CheckState State, string Detail) DeviceKey,
    (CheckState State, string Detail) Pioneer,
    (CheckState State, string Detail) Sql,
    PioneerRxDiscovery.DiscoveryResult? PioneerResult,
    SqlCredentialDiscovery.SqlCredentials? SqlCreds);
