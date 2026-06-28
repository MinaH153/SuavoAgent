using SuavoAgent.Setup;

namespace SuavoAgent.Setup.Gui.ViewModels;

/// <summary>
/// The "Connecting to your pharmacy…" step shown when the EXE was downloaded
/// from the dashboard with a single-use token baked into its filename
/// (<c>SuavoSetup-&lt;token&gt;.exe</c>). Exchanges that token for pharmacy
/// credentials OFF the UI thread (the call never runs inside SetupConfig.Load),
/// then hands the resulting <see cref="SetupConfig"/> to the normal install flow
/// via <c>onConnected</c>. On ANY failure — token expired/consumed, no network,
/// DNS, timeout, 5xx, malformed response — it falls back to device-code pairing
/// via <c>onFallback</c>. Never blocks the UI, never bricks the installer.
///
/// IO is injectable (<see cref="IInstallTokenService"/>) so the step is unit-
/// testable without a real network (mirrors DeviceCodePairingViewModel).
/// </summary>
internal sealed class ConnectingViewModel : ViewModelBase
{
    private readonly IInstallTokenService _service;
    private readonly string _token;
    private readonly string _cloudUrl;
    private readonly string _fingerprint;
    private readonly string _machineName;
    private readonly string _version;
    private readonly Action<SetupConfig> _onConnected;
    private readonly Action _onFallback;

    public ConnectingViewModel(
        IInstallTokenService service,
        string token,
        string cloudUrl,
        string fingerprint,
        string machineName,
        string version,
        Action<SetupConfig> onConnected,
        Action onFallback)
    {
        _service = service;
        _token = token;
        _cloudUrl = cloudUrl;
        _fingerprint = fingerprint;
        _machineName = machineName;
        _version = version;
        _onConnected = onConnected;
        _onFallback = onFallback;
    }

    private string _statusText = "Connecting to your pharmacy…";
    public string StatusText
    {
        get => _statusText;
        private set => SetField(ref _statusText, value);
    }

    /// <summary>Fire-and-forget from the view; awaitable in tests.</summary>
    public async Task StartAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var result = await _service
                .ExchangeAsync(_token, _machineName, _fingerprint, _version, cts.Token)
                .ConfigureAwait(true);

            // /register doesn't return brain (reasoning) config, so filename
            // installs are rules-only (fail-soft, the explicitly-supported
            // Reasoning=null case) — same brain posture as a no-config install.
            var config = new SetupConfig(
                PharmacyId: result.PharmacyId,
                ApiKey: result.ApiKey,
                CloudUrl: _cloudUrl,
                ReleaseTag: $"v{_version}",
                LearningMode: false,
                AgentId: result.AgentId);

            StatusText = "Connected — finishing setup…";
            _onConnected(config);
        }
        catch
        {
            // Every failure mode (no network, DNS, timeout, 4xx token expired/
            // consumed, 5xx, malformed) → device-code pairing. Never brick.
            _onFallback();
        }
    }
}
