using Microsoft.Extensions.Hosting;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Config;

namespace SuavoAgent.Core.Workers;

/// <summary>
/// Polls the cloud config-override endpoint on a fixed interval and writes
/// the result to a local JSON file. The host configuration pipeline layers
/// that file on top of appsettings.json via AddJsonFile(reloadOnChange: true)
/// so IOptionsMonitor-aware consumers pick up changes without a restart.
/// Simple consumers that read IOptions once at boot pick up changes at
/// next service restart.
///
/// Never throws out of ExecuteAsync — every iteration is wrapped so a
/// transient network blip (or broken cloud response) doesn't kill the
/// worker and leave the agent without future updates.
/// </summary>
public sealed class ConfigSyncWorker : BackgroundService
{
    private readonly IAgentConfigClient _client;
    private readonly ConfigOverrideStore _store;
    private readonly ConfigSyncOptions _opts;
    private readonly ILogger<ConfigSyncWorker> _logger;

    public ConfigSyncWorker(
        IAgentConfigClient client,
        ConfigOverrideStore store,
        ConfigSyncOptions opts,
        ILogger<ConfigSyncWorker> logger)
    {
        _client = client;
        _store = store;
        _opts = opts;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Stagger first fetch so we don't race startup of every pharmacy
        // agent hitting cloud in lockstep after a deploy.
        try
        {
            await Task.Delay(_opts.InitialDelay, stoppingToken);
        }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var resp = await _client.FetchAsync(stoppingToken);
                if (resp != null)
                {
                    _store.Apply(resp.Overrides);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ConfigSyncWorker: iteration failed (continuing)");
            }

            try
            {
                await Task.Delay(_opts.PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) { return; }
        }
    }
}

/// <summary>
/// Tunables for <see cref="ConfigSyncWorker"/>. Defaults: initial delay 15 s
/// (small skew + some cushion while other workers boot), poll every 5 min.
/// </summary>
public sealed class ConfigSyncOptions
{
    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromSeconds(15);
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(5);
}
