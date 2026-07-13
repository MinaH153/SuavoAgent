using Microsoft.Extensions.Hosting;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Health;
using SuavoAgent.Diagnostics;

namespace SuavoAgent.Core.Workers;

/// <summary>
/// Polls the cloud config-override endpoint on a fixed interval and writes
/// the result to a local JSON file. The host configuration pipeline layers
/// that file on top of appsettings.json via AddJsonFile(reloadOnChange: true)
/// so IOptionsMonitor-aware consumers pick up changes without a restart.
/// Simple consumers that read IOptions once at boot pick up changes at
/// next service restart.
///
/// Comp 2.2 — when the optional ruleset OTA dependencies are wired
/// (<see cref="IRulesetClient"/>, <see cref="RulesetSyncStore"/>,
/// <see cref="IRulesetVerifier"/>, <see cref="IRulesetSwapper"/>), the
/// same polling loop also fetches+verifies+swaps the signed ruleset.
/// Independent retry paths so a ruleset failure doesn't break the
/// config-override sync (or vice versa) per Comp 2 design §B.
///
/// Never throws out of ExecuteAsync — every iteration is wrapped so a
/// transient network blip (or broken cloud response) doesn't kill the
/// worker and leave the agent without future updates.
/// </summary>
public sealed class ConfigSyncWorker : ResilientHostedService
{
    private readonly IAgentConfigClient _client;
    private readonly ConfigOverrideStore _store;
    private readonly ConfigSyncOptions _opts;
    private readonly ILogger<ConfigSyncWorker> _logger;

    // Comp 2.2 — optional ruleset OTA deps. All four MUST be supplied
    // together; if any is null, the ruleset poll is silently skipped (the
    // agent runs on the embedded Phase 1 ruleset).
    private readonly IRulesetClient? _rulesetClient;
    private readonly RulesetSyncStore? _rulesetStore;
    private readonly IRulesetVerifier? _rulesetVerifier;
    private readonly IRulesetSwapper? _rulesetSwapper;
    private string? _currentRulesetETag;
    private bool _rulesetInitialised;

    public ConfigSyncWorker(
        IAgentConfigClient client,
        ConfigOverrideStore store,
        ConfigSyncOptions opts,
        ILogger<ConfigSyncWorker> logger,
        IRulesetClient? rulesetClient = null,
        RulesetSyncStore? rulesetStore = null,
        IRulesetVerifier? rulesetVerifier = null,
        IRulesetSwapper? rulesetSwapper = null,
        WorkerHealthRegistry? healthRegistry = null)
        : base(logger, healthRegistry)
    {
        _client = client;
        _store = store;
        _opts = opts;
        _logger = logger;
        _rulesetClient = rulesetClient;
        _rulesetStore = rulesetStore;
        _rulesetVerifier = rulesetVerifier;
        _rulesetSwapper = rulesetSwapper;
    }

    /// <summary>True when all four OTA deps were supplied at construction.</summary>
    internal bool RulesetOtaEnabled
        => _rulesetClient is not null
           && _rulesetStore is not null
           && _rulesetVerifier is not null
           && _rulesetSwapper is not null;

    protected override string WorkerName => "config-sync";

    // Config-poll worker: supervise unconditionally. Deliberately NOT gated by
    // SelfHeal.WorkerSupervisorEnabled (it has no AgentOptions dependency, and the poll loop is
    // low blast radius) — escalation after MaxRestarts still prevents a runaway restart loop.
    protected override bool RestartOnFault => true;

    protected override Task OnEscalateAsync()
    {
        _logger.LogCritical("ConfigSyncWorker exhausted supervised restarts — config sync halted");
        return Task.CompletedTask;
    }

    protected override async Task RunAsync(CancellationToken stoppingToken)
    {
        await InitializeRulesetCacheAsync(stoppingToken).ConfigureAwait(false);
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
                _logger.LogSafeWarning(ex);
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
                    _logger.LogSafeDebug(ex);
                }
            }

            // Comp 2.2 — independent ruleset poll. Wrapped so a ruleset
            // failure doesn't break the config-override flow.
            if (RulesetOtaEnabled)
            {
                try
                {
                    await PollRulesetAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogSafeWarning(ex);
                }
            }

            try
            {
                await Task.Delay(_opts.PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// Once-per-process: sweep orphan temps, load the cached bundle (if
    /// any), verify its signature, and swap-in the cache only when its
    /// monotonic version int exceeds the embedded floor. Comp 2 design §B
    /// cache-vs-embedded boot logic (round-6 HIGH).
    /// </summary>
    internal async Task InitializeRulesetCacheAsync(CancellationToken ct)
    {
        if (!RulesetOtaEnabled || _rulesetInitialised)
        {
            return;
        }
        _rulesetInitialised = true;

        try
        {
            await _rulesetStore!.InitializeAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogSafeWarning(ex);
            return;
        }

        var cached = _rulesetStore.TryLoadCurrent();
        if (cached is null)
        {
            return;
        }

        var verify = _rulesetVerifier!.Verify(cached);
        if (!verify.IsValid)
        {
            // Cached bundle no longer trusted (rotated-out key_id, expired,
            // or corrupted). Alarm + fall back to embedded.
            _logger.LogWarning(
                "ConfigSyncWorker: cached ruleset verify failed: {Reason}",
                verify.FailureReason);
            Wire.ReportInvariant("ruleset.cache_verify_failed",
                new Dictionary<string, string>
                {
                    ["reason"] = verify.FailureReason ?? "unknown",
                    ["cached_key_id"] = cached.Ruleset.KeyId,
                });
            return;
        }

        // Cache-vs-embedded: cache wins only on strict greater
        // ruleset_version_int (round-5 LOW tie-break — equal prefers
        // embedded, defends against stale-cache-after-rollback).
        var embeddedInt = _rulesetSwapper!.CurrentVersionInt;
        var cacheInt = cached.Ruleset.RulesetVersionInt;
        if (cacheInt > embeddedInt)
        {
            _rulesetSwapper.Swap(cached.Ruleset);
            _currentRulesetETag = string.IsNullOrEmpty(cached.ETag) ? null : cached.ETag;
            _logger.LogInformation(
                "ConfigSyncWorker: adopted cached ruleset version_int={CacheInt} (embedded was {EmbeddedInt})",
                cacheInt, embeddedInt);
        }
    }

    /// <summary>
    /// One ruleset poll iteration: fetch → outcome classify → verify →
    /// freshness gate → persist → swap. Maps each failure mode to the
    /// design's preserve-cache / alarm contract (Comp 2 design §B).
    /// </summary>
    internal async Task PollRulesetAsync(CancellationToken ct)
    {
        if (!RulesetOtaEnabled)
        {
            return;
        }

        var result = await _rulesetClient!.FetchAsync(_currentRulesetETag, ct).ConfigureAwait(false);
        switch (result.Outcome)
        {
            case RulesetFetchOutcome.NotModified:
                return;
            case RulesetFetchOutcome.AuthFailed:
                Wire.ReportInvariant("ruleset.auth_failed",
                    new Dictionary<string, string>
                    {
                        ["kind"] = _rulesetClient.LastFailureKind ?? "unknown",
                    });
                return;
            case RulesetFetchOutcome.Transient:
                return;  // no alarm — transient network / cloud blip
            case RulesetFetchOutcome.SchemaInvalid:
                Wire.ReportInvariant("ruleset.schema_drift",
                    new Dictionary<string, string>
                    {
                        ["kind"] = _rulesetClient.LastFailureKind ?? "unknown",
                    });
                return;
            case RulesetFetchOutcome.Updated:
                break;
        }

        var bundle = result.Bundle!;

        var verify = _rulesetVerifier!.Verify(bundle);
        if (!verify.IsValid)
        {
            Wire.ReportInvariant("ruleset.signature_verify_failed",
                new Dictionary<string, string>
                {
                    ["reason"] = verify.FailureReason ?? "unknown",
                    ["key_id"] = bundle.Ruleset.KeyId,
                });
            return;
        }

        // OTA-time freshness gate (Codex round-8 HIGH): the cloud may
        // have a bug or accidentally serve a stale/older bundle. Reject
        // anything that isn't strictly greater than the in-memory version.
        if (bundle.Ruleset.RulesetVersionInt <= _rulesetSwapper!.CurrentVersionInt)
        {
            Wire.ReportInvariant("ruleset.ota_int_regression",
                new Dictionary<string, string>
                {
                    ["cloud_int"] = bundle.Ruleset.RulesetVersionInt.ToString(),
                    ["agent_int"] = _rulesetSwapper.CurrentVersionInt.ToString(),
                });
            return;
        }

        // Persist before swap so a process crash between swap + save
        // leaves the disk cache pointing at the OLD (still-valid) bundle.
        // Disk failure does NOT block swap — the in-memory ruleset advances
        // and the next restart loads the embedded fallback.
        var saved = true;
        try
        {
            await _rulesetStore!.SaveAsync(bundle, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            saved = false;
            _logger.LogSafeWarning(ex);
            Wire.ReportInvariant("ruleset.disk_write_failed",
                new Dictionary<string, string>
                {
                    ["reason"] = ex.GetType().Name,
                });
        }

        _rulesetSwapper.Swap(bundle.Ruleset);
        if (saved)
        {
            _currentRulesetETag = string.IsNullOrEmpty(bundle.ETag) ? null : bundle.ETag;
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
