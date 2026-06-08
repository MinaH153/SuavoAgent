using SuavoAgent.Core.Agentic;

namespace SuavoAgent.Core.Workers;

/// <summary>
/// The always-on decay-of-the-unused: a periodic sweep that evaporates EVERY known edge in EVERY
/// (pharmacy, task) scope toward the Floor, mirroring <c>LearningWorker</c>'s 5-minute tick. This is the
/// slime-mold dynamic that prunes stale paths and auto-handles UI drift — a path that stops being
/// verified fades and its replacement overtakes it. Reinforcement (thickening) happens elsewhere
/// (post-run <see cref="EdgeReinforcement.ApplyRun"/>); this worker only ever DECAYS, so it never races a
/// run for "who wins an edge" — and the store serializes the writes internally.
/// </summary>
public sealed class PhysarumEvaporationWorker : BackgroundService
{
    private readonly IEdgeConductanceStore _store;
    private readonly ILogger<PhysarumEvaporationWorker> _logger;
    private readonly ConductanceParams _params;
    private readonly TimeSpan _interval;

    public PhysarumEvaporationWorker(
        IEdgeConductanceStore store,
        ILogger<PhysarumEvaporationWorker> logger,
        ConductanceParams? conductanceParams = null,
        TimeSpan? interval = null)
    {
        _store = store;
        _logger = logger;
        _params = conductanceParams ?? ConductanceParams.Default;
        _interval = interval ?? TimeSpan.FromMinutes(5);
    }

    /// <summary>One evaporation sweep across every known scope. Best-effort per scope — a failing scope is
    /// logged and the sweep continues (never lets one bad scope stall the rest). Returns scopes swept.</summary>
    public int EvaporateOnce()
    {
        var swept = 0;
        foreach (var (pharmacyId, taskKey) in _store.AllPharmacyTaskPairs())
        {
            try
            {
                EdgeReinforcement.Evaporate(_store, pharmacyId, taskKey, _params);
                swept++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "physarum evaporation failed for scope {Pharmacy}/{Task}", pharmacyId, taskKey);
            }
        }
        return swept;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                var swept = EvaporateOnce();
                if (swept > 0)
                    _logger.LogDebug("physarum evaporation tick: swept {Scopes} scope(s)", swept);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "physarum evaporation tick failed");
            }
        }
    }
}
