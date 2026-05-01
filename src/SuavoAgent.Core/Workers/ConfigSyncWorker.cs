using Microsoft.Extensions.Hosting;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Health;

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
        while (!stoppingToken.IsCancellationRequested)
        {
            var attemptAt = DateTimeOffset.UtcNow;
            var status = "failed";
            string? errorKind = null;
            try
            {
                var resp = await _client.FetchAsync(stoppingToken);
                if (resp != null)
                {
                    _store.Apply(resp.Overrides);
                    _opts.LastAppliedOverrideCount = resp.Overrides.Count;
                    _opts.ConsecutiveFailures = 0;
                    _opts.LastSuccessAt = attemptAt;
                    status = "ok";
                }
                else
                {
                    _opts.ConsecutiveFailures++;
                    errorKind = _client.LastFailureKind ?? "fetch_returned_null";
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _opts.ConsecutiveFailures++;
                errorKind = ex.GetType().Name;
                _logger.LogWarning(ex, "ConfigSyncWorker: iteration failed (continuing)");
            }
            finally
            {
                try
                {
                    RuntimeHealthEvidence.WriteConfigSyncHealth(
                        _opts.HealthPath,
                        status,
                        attemptAt,
                        _opts.LastSuccessAt,
                        _opts.ConsecutiveFailures,
                        errorKind,
                        _opts.LastAppliedOverrideCount);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "ConfigSyncWorker: health write failed");
                }
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
/// Tunables for <see cref="ConfigSyncWorker"/>. The first fetch is immediate
/// so a fresh restart does not run with stale or empty overrides; PollInterval
/// controls later refreshes.
/// </summary>
public sealed class ConfigSyncOptions
{
    public TimeSpan InitialDelay { get; set; } = TimeSpan.Zero;
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(5);
    public string HealthPath { get; set; } = RuntimeHealthEvidence.ConfigSyncHealthPath();
    public DateTimeOffset? LastSuccessAt { get; set; }
    public int ConsecutiveFailures { get; set; }
    public int LastAppliedOverrideCount { get; set; }
}
