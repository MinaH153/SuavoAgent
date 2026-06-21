using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
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
        CheckState.Warn => "#E8B65C",      // amber — attention
        CheckState.Fail => "#C95454",      // wine — blocks
        CheckState.Deferred => "#C9A24C",  // muted gold — self-configures
        _ => "#6E6A62",                    // subtle — still scanning
    };

    /// <summary>Right-aligned pill text — the human verdict for this row.</summary>
    public string StatusLabel => _state switch
    {
        CheckState.Ok => "Ready",
        CheckState.Warn => Tier == CheckTier.Required ? "Required" : "Recommended",
        CheckState.Fail => "Required",
        CheckState.Deferred => "Self-configures",
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
    private bool _isReady;

    // Required = the only thing the binaries truly need to land + run.
    public CheckItem OsCheck { get; } = new("Windows 10 / 11", CheckTier.Required);
    public CheckItem DiskCheck { get; } = new("Disk space (≥ 2 GB)", CheckTier.Required);
    public CheckItem RuntimeCheck { get; } = new("Runtime components", CheckTier.Required);
    // Informational = the agent self-heals or only-recommends these.
    public CheckItem BitLockerCheck { get; } = new("BitLocker status", CheckTier.Informational);
    public CheckItem PioneerCheck { get; } = new("PioneerRx installation", CheckTier.Informational);
    public CheckItem SqlCheck { get; } = new("SQL Server credentials", CheckTier.Informational);

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
        CheckState.Fail => "This workstation can't run SuavoAgent",
        _ when !IsReady => "Checking this workstation…",
        _ => "Ready to install",
    };

    public string ReadinessDetail => OsCheck.State switch
    {
        CheckState.Fail => "Windows 10 or newer (64-bit) is required.",
        _ when !IsReady => "Confirming the essentials. The rest configures itself.",
        _ => "PioneerRx and SQL connect automatically once they're detected.",
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
        _ when !IsReady => "#C9A24C",
        _ => "#7A9B6E",
    };

    public SystemCheckViewModel(
        InstallContext ctx,
        Action onReady,
        Func<bool>? probeIsWindows10 = null,
        Func<PioneerRxDiscovery.DiscoveryResult?>? probePioneer = null,
        Func<string, SqlCredentialDiscovery.SqlCredentials?>? probeSql = null,
        Func<CancellationToken, Task<(CheckState, string)>>? probeRuntime = null)
    {
        _ctx = ctx;
        _onReady = onReady;
        // Probes are injectable so the scan is unit-testable without a Windows box;
        // defaults are the real Win32/registry/SQL implementations.
        _probeIsWindows10 = probeIsWindows10 ?? (() => OperatingSystem.IsWindowsVersionAtLeast(10));
        _probePioneer = probePioneer ?? PioneerRxDiscovery.Discover;
        _probeSql = probeSql ?? SqlCredentialDiscovery.TryAutoDiscover;
        _probeRuntime = probeRuntime ?? RealProbeRuntime;

        Items = new ObservableCollection<CheckItem>
        {
            OsCheck, DiskCheck, RuntimeCheck, BitLockerCheck, PioneerCheck, SqlCheck,
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
    /// Minimum-viable-control gate: every Required check has resolved to a
    /// non-blocking state, and none is still scanning. PioneerRx / SQL absence
    /// is <see cref="CheckState.Deferred"/>, never a blocker — the agent
    /// self-heals them after it's online.
    /// RuntimeCheck must specifically be Ok (not just non-Fail) — the VC++ runtime
    /// is a hard binary dependency; Warn is not a valid install state for it.
    /// </summary>
    private void RecomputeReadiness() =>
        IsReady = Items.All(i => i.State != CheckState.Pending)
                  && Items.Where(i => i.Tier == CheckTier.Required)
                          .All(i => i.State != CheckState.Fail)
                  && RuntimeCheck.State == CheckState.Ok;

    /// <summary>
    /// Runs every probe on a background thread (manage-bde, registry, SQL — all
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
        var disk = ProbeDisk();
        // ProbeRuntime may download+install the VC++ redistributable — it is async internally
        // but called with .GetAwaiter().GetResult() because Probe() is always invoked from a
        // background thread (inside Task.Run), never from the UI thread.
        var runtime = _probeRuntime(CancellationToken.None).GetAwaiter().GetResult();
        var bitLocker = ProbeBitLocker();
        var (pioneerState, pioneerDetail, pioneer) = ProbePioneer();
        var (sqlState, sqlDetail, sqlCreds) = ProbeSql(pioneer);
        return new ProbeOutcome(
            os, disk, runtime, bitLocker,
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
                : (CheckState.Fail, "Windows 10 or newer (64-bit) required.");
        }
        catch
        {
            // Can't confirm a compatible OS → block (the one hard requirement).
            return (CheckState.Fail, "Could not confirm Windows version.");
        }
    }

    private (CheckState, string) ProbeDisk()
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(_ctx.InstallDir) ?? "C:\\");
            var freeGb = drive.AvailableFreeSpace / (1024.0 * 1024 * 1024);
            return freeGb >= 2
                ? (CheckState.Ok, $"{freeGb:F1} GB free on {drive.Name}")
                : (CheckState.Warn, $"Only {freeGb:F1} GB free — install may be tight.");
        }
        catch (Exception ex)
        {
            return (CheckState.Warn, ex.Message);
        }
    }

    private (CheckState, string) ProbeBitLocker()
    {
        // Off is a loud recommendation (PHI-at-rest), not a blocker.
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("manage-bde", "-status C:")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            var output = proc?.StandardOutput.ReadToEnd() ?? string.Empty;
            proc?.WaitForExit(5000);
            return output.Contains("Protection On", StringComparison.OrdinalIgnoreCase)
                ? (CheckState.Ok, "BitLocker protection enabled on C:")
                : (CheckState.Warn, "PHI at rest is unencrypted — enable BitLocker on C: (HIPAA).");
        }
        catch
        {
            return (CheckState.Warn, "Could not query BitLocker. Continuing.");
        }
    }

    private (CheckState, string, PioneerRxDiscovery.DiscoveryResult?) ProbePioneer()
    {
        // Absence is deferred, not a failure — the agent watches for it and
        // connects automatically once it appears.
        try
        {
            var pioneer = _probePioneer();
            return pioneer != null
                ? (CheckState.Ok, pioneer.PioneerDir, pioneer)
                : (CheckState.Deferred,
                   "Not detected yet — SuavoAgent connects automatically once PioneerRx is installed.",
                   (PioneerRxDiscovery.DiscoveryResult?)null);
        }
        catch
        {
            return (CheckState.Deferred,
                "Detection deferred — SuavoAgent will keep watching and connect automatically.",
                null);
        }
    }

    private (CheckState, string, SqlCredentialDiscovery.SqlCredentials?) ProbeSql(
        PioneerRxDiscovery.DiscoveryResult? pioneer)
    {
        if (pioneer == null)
            return (CheckState.Deferred, "Configures itself once PioneerRx is detected.", null);

        try
        {
            var creds = _probeSql(pioneer.PioneerConfig);
            return creds != null
                ? (CheckState.Ok,
                   $"{creds.Server} / {creds.Database} ({(creds.IsWindowsAuth ? "Windows" : $"SQL: {creds.User}")})",
                   creds)
                : (CheckState.Warn,
                   "Auto-discovery failed — you'll enter credentials manually.",
                   (SqlCredentialDiscovery.SqlCredentials?)null);
        }
        catch
        {
            return (CheckState.Warn, "Auto-discovery failed — you'll enter credentials manually.", null);
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
            ).EnsureAsync(Path.GetTempPath(), ct);

            return outcome.State == VcRedistPreflightState.Failed
                ? (CheckState.Fail, outcome.Detail)
                : (CheckState.Ok, outcome.Detail);
        }
        catch (Exception ex)
        {
            return (CheckState.Fail,
                $"Runtime check failed: {ex.Message}. Install vc_redist.x64.exe manually, then retry.");
        }
    }
}

/// <summary>Immutable result of one background scan — applied to the VM on the UI thread.</summary>
internal sealed record ProbeOutcome(
    (CheckState State, string Detail) Os,
    (CheckState State, string Detail) Disk,
    (CheckState State, string Detail) Runtime,
    (CheckState State, string Detail) BitLocker,
    (CheckState State, string Detail) Pioneer,
    (CheckState State, string Detail) Sql,
    PioneerRxDiscovery.DiscoveryResult? PioneerResult,
    SqlCredentialDiscovery.SqlCredentials? SqlCreds);
