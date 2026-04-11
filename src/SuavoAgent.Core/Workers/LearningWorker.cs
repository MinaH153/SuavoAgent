using Microsoft.Extensions.Options;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Learning;
using SuavoAgent.Core.State;

namespace SuavoAgent.Core.Workers;

/// <summary>
/// Orchestrates the 30-day learning phases. Manages observer lifecycle,
/// phase transitions, and mode promotions. Only runs when LearningMode = true.
/// </summary>
public sealed class LearningWorker : BackgroundService
{
    private readonly ILogger<LearningWorker> _logger;
    private readonly AgentOptions _options;
    private readonly AgentStateDb _db;
    private readonly IServiceProvider _sp;
    private readonly List<ILearningObserver> _observers = new();
    private string? _sessionId;

    public LearningWorker(
        ILogger<LearningWorker> logger,
        IOptions<AgentOptions> options,
        AgentStateDb db,
        IServiceProvider sp)
    {
        _logger = logger;
        _options = options.Value;
        _db = db;
        _sp = sp;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.LearningMode)
        {
            _logger.LogInformation("Learning mode disabled — LearningWorker idle");
            return;
        }

        _sessionId = $"learn-{_options.AgentId}-{DateTimeOffset.UtcNow:yyyyMMdd}";
        var pharmacyId = _options.PharmacyId ?? "unknown";
        var pharmacySalt = _options.AgentId ?? "default-salt";

        // Create or resume learning session
        var existing = _db.GetLearningSession(_sessionId);
        if (existing is null)
        {
            _db.CreateLearningSession(_sessionId, pharmacyId);
            _logger.LogInformation("Created learning session {Id} for pharmacy {Pharmacy}",
                _sessionId, pharmacyId);
        }

        // Initialize observers
        var processObs = new ProcessObserver(_db, pharmacySalt,
            _sp.GetRequiredService<ILogger<ProcessObserver>>());
        var sqlObs = new SqlSchemaObserver(_db, pharmacySalt,
            _sp.GetRequiredService<ILogger<SqlSchemaObserver>>());

        _observers.Add(processObs);
        _observers.Add(sqlObs);

        _db.AppendLearningAudit(_sessionId, "worker", "start",
            $"observers:{_observers.Count}", phiScrubbed: false);

        // Start observers for current phase
        var session = _db.GetLearningSession(_sessionId)!.Value;
        var currentPhase = LearningSession.PhaseToObserverPhase(session.Phase);

        foreach (var obs in _observers)
        {
            if (obs.ActivePhases.HasFlag(currentPhase))
            {
                _ = obs.StartAsync(_sessionId, stoppingToken);
                _logger.LogInformation("Started observer: {Name}", obs.Name);
            }
        }

        // Phase management loop
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);

            session = _db.GetLearningSession(_sessionId)!.Value;

            // Check observer health — hard stop if any fails
            foreach (var obs in _observers)
            {
                var health = obs.CheckHealth();
                if (obs.ActivePhases.HasFlag(currentPhase) && !health.IsRunning)
                {
                    _logger.LogWarning("Observer {Name} stopped unexpectedly — flagging anomaly",
                        health.ObserverName);
                    _db.AppendLearningAudit(_sessionId, "worker", "observer_health_fail",
                        health.ObserverName, phiScrubbed: false);

                    // If in autonomous mode, hard stop
                    if (session.Mode == "autonomous")
                    {
                        _logger.LogWarning("HARD STOP: observer failure in autonomous mode — downgrading to supervised");
                        _db.UpdateLearningMode(_sessionId, "supervised");
                    }
                }
            }

            // Phase auto-advance is manual for now — operator triggers via signed command
            // Future: auto-advance based on LearningSession.GetNextPhase()
        }

        // Cleanup
        foreach (var obs in _observers)
        {
            await obs.StopAsync();
            obs.Dispose();
        }

        _logger.LogInformation("LearningWorker stopped");
    }
}
