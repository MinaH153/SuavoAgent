using System.Diagnostics;
using System.Windows.Input;

namespace SuavoAgent.Setup.Gui.ViewModels;

/// <summary>
/// First consumer of <see cref="DeviceCodePairing"/> (slice C2a): the GUI screen
/// shown when the native installer starts. It creates an 8-character
/// device code, shows it to the operator (who approves it in the dashboard), and
/// polls until authorized — then hands the resulting <see cref="SetupConfig"/>
/// back so the existing install flow can run.
///
/// All IO + time is injectable so the screen is unit-testable without a real
/// network or waits (mirrors DeviceCodePairing's own test seam).
/// </summary>
internal sealed class DeviceCodePairingViewModel : ViewModelBase
{
    private readonly IDeviceCodeService _service;
    private readonly string _cloudUrl;
    private readonly string _fingerprint;
    private readonly string _version;
    private readonly Action<SetupConfig> _onPaired;
    private readonly Action _onCancelled;
    private readonly Func<TimeSpan, CancellationToken, Task>? _delay;
    private readonly Action<string> _openUrl;

    private CancellationTokenSource? _cts;

    public DeviceCodePairingViewModel(
        IDeviceCodeService service,
        string cloudUrl,
        string fingerprint,
        string version,
        Action<SetupConfig> onPaired,
        Action onCancelled,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Action<string>? openUrl = null)
    {
        _service = service;
        _cloudUrl = cloudUrl;
        _fingerprint = fingerprint;
        _version = version;
        _onPaired = onPaired;
        _onCancelled = onCancelled;
        _delay = delay;
        _openUrl = openUrl ?? OpenUrlDefault;

        OpenBrowserCommand = new RelayCommand(
            OpenBrowser,
            () => !string.IsNullOrEmpty(VerificationUrl));
        CancelCommand = new RelayCommand(Cancel);
        RetryCommand = new RelayCommand(() => _ = StartAsync(), () => IsFailed);
    }

    // ── Observable state ───────────────────────────────────────────────────

    private string _deviceCode = "";
    public string DeviceCode
    {
        get => _deviceCode;
        private set => SetField(ref _deviceCode, value);
    }

    private string _verificationUrl = "";
    public string VerificationUrl
    {
        get => _verificationUrl;
        private set
        {
            if (SetField(ref _verificationUrl, value))
                (OpenBrowserCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    private int _secondsRemaining;
    public int SecondsRemaining
    {
        get => _secondsRemaining;
        private set
        {
            if (SetField(ref _secondsRemaining, value))
                RaisePropertyChanged(nameof(TimeRemainingText));
        }
    }

    public string TimeRemainingText =>
        SecondsRemaining > 0 ? $"Expires in {SecondsRemaining / 60}:{SecondsRemaining % 60:D2}" : "";

    private string _statusText = "Starting…";
    public string StatusText
    {
        get => _statusText;
        private set => SetField(ref _statusText, value);
    }

    private bool _isWorking;
    public bool IsWorking
    {
        get => _isWorking;
        private set => SetField(ref _isWorking, value);
    }

    private bool _isFailed;
    public bool IsFailed
    {
        get => _isFailed;
        private set
        {
            if (SetField(ref _isFailed, value))
            {
                RaisePropertyChanged(nameof(ShowCode));
                (RetryCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    private string _errorText = "";
    public string ErrorText
    {
        get => _errorText;
        private set => SetField(ref _errorText, value);
    }

    /// <summary>The code panel is hidden once pairing fails (the retry CTA takes over).</summary>
    public bool ShowCode => !IsFailed;

    public ICommand OpenBrowserCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand RetryCommand { get; }

    // ── Flow ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs (or re-runs) pairing: create a fresh code, then poll until the
    /// dashboard authorizes. Fire-and-forget from the view; awaitable in tests.
    /// </summary>
    public async Task StartAsync()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        IsFailed = false;
        ErrorText = "";
        IsWorking = true;
        DeviceCode = "";
        StatusText = "Generating a pairing code…";

        var pairing = new DeviceCodePairing(_service, _cloudUrl, _delay);
        var progress = new Progress<PairingProgress>(OnProgress);

        try
        {
            var result = await pairing.RunAsync(_fingerprint, _version, progress, ct)
                .ConfigureAwait(true);
            if (ct.IsCancellationRequested) return;

            if (result.Authorized && result.Config != null)
            {
                IsWorking = false;
                StatusText = "Workstation approved — finishing setup…";
                _onPaired(result.Config);
            }
            else
            {
                Fail(result.Reason);
            }
        }
        catch (OperationCanceledException)
        {
            // User cancelled — Cancel() already drives the next step.
        }
        catch (DeviceAuthorityUnavailableException)
        {
            Fail("device_authority_unavailable");
        }
        catch (Exception)
        {
            Fail("unexpected_failure");
        }
    }

    private void OnProgress(PairingProgress p)
    {
        DeviceCode = p.DeviceCode;
        VerificationUrl = p.VerificationUrl;
        SecondsRemaining = p.SecondsRemaining;
        StatusText = p.Status switch
        {
            "pending" => "Waiting for approval in your dashboard…",
            "authorized" => "Approved!",
            _ => "Waiting for secure approval…",
        };
    }

    private void Fail(string reason)
    {
        IsWorking = false;
        IsFailed = true;
        StatusText = "Pairing didn’t complete";
        ErrorText = reason switch
        {
            "expired" =>
                "This code expired. Generate a fresh one and approve it in the dashboard within 15 minutes.",
            "denied" => "This code was denied in the dashboard.",
            "transient_retry_exhausted" =>
                "The pairing service could not be reached reliably. Check the internet connection and try again.",
            "device_authority_unavailable" =>
                "This PC could not create its secure device key. Enable TPM 2.0 in firmware, resolve any TPM warning in Windows Security, then restart Setup.",
            _ => "Pairing didn’t complete. Try a new code. Support code: SETUP-PAIRING-UNEXPECTED",
        };
    }

    private void Cancel()
    {
        _cts?.Cancel();
        _onCancelled();
    }

    private void OpenBrowser()
    {
        if (string.IsNullOrEmpty(VerificationUrl)) return;
        try
        {
            _openUrl(VerificationUrl);
        }
        catch
        {
            StatusText =
                "The browser could not open. Copy the secure dashboard link shown below.";
        }
    }

    private static void OpenUrlDefault(string url)
    {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
}
