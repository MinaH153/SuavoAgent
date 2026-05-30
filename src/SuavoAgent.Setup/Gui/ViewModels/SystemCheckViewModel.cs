using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using SuavoAgent.Setup.Gui.Services;

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
    private readonly InstallContext _ctx;
    private readonly Action _onReady;
    private bool _isReady;

    // Required = the only thing the binaries truly need to land + run.
    public CheckItem OsCheck { get; } = new("Windows 10 / 11", CheckTier.Required);
    public CheckItem DiskCheck { get; } = new("Disk space (≥ 2 GB)", CheckTier.Required);
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

    public SystemCheckViewModel(InstallContext ctx, Action onReady)
    {
        _ctx = ctx;
        _onReady = onReady;
        Items = new ObservableCollection<CheckItem>
        {
            OsCheck, DiskCheck, BitLockerCheck, PioneerCheck, SqlCheck,
        };

        // Readiness is a live function of the probe states, not a one-shot at the
        // end of the scan — so the Continue button lights up the moment the
        // required checks pass, regardless of the self-healing ones.
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
    /// </summary>
    private void RecomputeReadiness() =>
        IsReady = Items.All(i => i.State != CheckState.Pending)
                  && Items.Where(i => i.Tier == CheckTier.Required)
                          .All(i => i.State != CheckState.Fail);

    /// <summary>
    /// Runs every probe on a background thread. Thread-hops back to the UI
    /// via property setters, which raise INotifyPropertyChanged on the
    /// dispatcher Avalonia is already listening on.
    /// </summary>
    public Task RunChecksAsync() => Task.Run(RunChecks);

    private void RunChecks()
    {
        // OS — the one true hard requirement.
        if (OperatingSystem.IsWindowsVersionAtLeast(10))
        {
            OsCheck.State = CheckState.Ok;
            OsCheck.Detail = Environment.OSVersion.VersionString;
        }
        else
        {
            OsCheck.State = CheckState.Fail;
            OsCheck.Detail = "Windows 10 or newer required.";
        }

        // Disk
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(_ctx.InstallDir) ?? "C:\\");
            var freeGb = drive.AvailableFreeSpace / (1024.0 * 1024 * 1024);
            if (freeGb >= 2)
            {
                DiskCheck.State = CheckState.Ok;
                DiskCheck.Detail = $"{freeGb:F1} GB free on {drive.Name}";
            }
            else
            {
                DiskCheck.State = CheckState.Warn;
                DiskCheck.Detail = $"Only {freeGb:F1} GB free — install may be tight.";
            }
        }
        catch (Exception ex)
        {
            DiskCheck.State = CheckState.Warn;
            DiskCheck.Detail = ex.Message;
        }

        // BitLocker — best-effort via manage-bde. Off is a loud recommendation
        // (PHI-at-rest), not a blocker — the operator may encrypt post-install.
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
            if (output.Contains("Protection On", StringComparison.OrdinalIgnoreCase))
            {
                BitLockerCheck.State = CheckState.Ok;
                BitLockerCheck.Detail = "BitLocker protection enabled on C:";
            }
            else
            {
                BitLockerCheck.State = CheckState.Warn;
                BitLockerCheck.Detail = "PHI at rest is unencrypted — enable BitLocker on C: (HIPAA).";
            }
        }
        catch
        {
            BitLockerCheck.State = CheckState.Warn;
            BitLockerCheck.Detail = "Could not query BitLocker. Continuing.";
        }

        // PioneerRx — absence is deferred, not a failure. The agent watches for
        // it and connects automatically once it appears.
        var pioneer = PioneerRxDiscovery.Discover();
        if (pioneer != null)
        {
            _ctx.Pioneer = pioneer;
            PioneerCheck.State = CheckState.Ok;
            PioneerCheck.Detail = pioneer.PioneerDir;
        }
        else
        {
            PioneerCheck.State = CheckState.Deferred;
            PioneerCheck.Detail = "Not detected yet — SuavoAgent connects automatically once PioneerRx is installed.";
        }

        // SQL
        if (pioneer != null)
        {
            var creds = SqlCredentialDiscovery.TryAutoDiscover(pioneer.PioneerConfig);
            if (creds != null)
            {
                _ctx.SqlCredentials = creds;
                SqlCheck.State = CheckState.Ok;
                SqlCheck.Detail = $"{creds.Server} / {creds.Database} ({(creds.IsWindowsAuth ? "Windows" : $"SQL: {creds.User}")})";
            }
            else
            {
                SqlCheck.State = CheckState.Warn;
                SqlCheck.Detail = "Auto-discovery failed — you'll enter credentials manually.";
            }
        }
        else
        {
            SqlCheck.State = CheckState.Deferred;
            SqlCheck.Detail = "Configures itself once PioneerRx is detected.";
        }

        // Final reconciliation in case no state actually changed during the scan
        // (e.g. everything was already Ok), so readiness reflects the result.
        RecomputeReadiness();
    }
}
