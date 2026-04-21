using System.Collections.ObjectModel;
using System.Windows.Input;
using SuavoAgent.Setup.Gui.Services;

namespace SuavoAgent.Setup.Gui.ViewModels;

public sealed class CheckItem : ViewModelBase
{
    private CheckState _state = CheckState.Pending;
    private string _detail = "Checking…";

    public CheckItem(string title) => Title = title;

    public string Title { get; }

    public CheckState State
    {
        get => _state;
        set
        {
            SetField(ref _state, value);
            RaisePropertyChanged(nameof(Icon));
            RaisePropertyChanged(nameof(ColorHex));
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
        CheckState.Fail => "✗",
        _ => "·",
    };

    public string ColorHex => _state switch
    {
        CheckState.Ok => "#7A9B6E",
        CheckState.Warn => "#E8B65C",
        CheckState.Fail => "#C95454",
        _ => "#6E6A62",
    };
}

public enum CheckState { Pending, Ok, Warn, Fail }

internal sealed class SystemCheckViewModel : ViewModelBase
{
    private readonly InstallContext _ctx;
    private readonly Action _onReady;
    private bool _isReady;

    public CheckItem OsCheck { get; } = new("Windows 10 / 11");
    public CheckItem DiskCheck { get; } = new("Disk space (≥ 2 GB)");
    public CheckItem BitLockerCheck { get; } = new("BitLocker status");
    public CheckItem PioneerCheck { get; } = new("PioneerRx installation");
    public CheckItem SqlCheck { get; } = new("SQL Server credentials");

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

    public SystemCheckViewModel(InstallContext ctx, Action onReady)
    {
        _ctx = ctx;
        _onReady = onReady;
        Items = new ObservableCollection<CheckItem>
        {
            OsCheck, DiskCheck, BitLockerCheck, PioneerCheck, SqlCheck,
        };
        ContinueCommand = new RelayCommand(_onReady, () => IsReady);
    }

    /// <summary>
    /// Runs every probe on a background thread. Thread-hops back to the UI
    /// via property setters, which raise INotifyPropertyChanged on the
    /// dispatcher Avalonia is already listening on.
    /// </summary>
    public Task RunChecksAsync() => Task.Run(RunChecks);

    private void RunChecks()
    {
        // OS
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

        // BitLocker — best-effort via manage-bde
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
                BitLockerCheck.Detail = "BitLocker not enabled — recommended for HIPAA.";
            }
        }
        catch
        {
            BitLockerCheck.State = CheckState.Warn;
            BitLockerCheck.Detail = "Could not query BitLocker. Continuing.";
        }

        // PioneerRx
        var pioneer = PioneerRxDiscovery.Discover();
        if (pioneer != null)
        {
            _ctx.Pioneer = pioneer;
            PioneerCheck.State = CheckState.Ok;
            PioneerCheck.Detail = pioneer.PioneerDir;
        }
        else
        {
            PioneerCheck.State = CheckState.Fail;
            PioneerCheck.Detail = "PioneerRx not installed on this machine.";
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
            SqlCheck.State = CheckState.Pending;
            SqlCheck.Detail = "Waiting on PioneerRx.";
        }

        IsReady = PioneerCheck.State == CheckState.Ok;
    }
}
