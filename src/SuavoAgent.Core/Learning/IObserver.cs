namespace SuavoAgent.Core.Learning;

public interface ILearningObserver : IDisposable
{
    string Name { get; }
    ObserverPhase ActivePhases { get; }
    Task StartAsync(string sessionId, CancellationToken ct);
    Task StopAsync();
    ObserverHealth CheckHealth();
}

[Flags]
public enum ObserverPhase
{
    Discovery = 1,
    Pattern = 2,
    Model = 4,
    Active = 8,
    All = Discovery | Pattern | Model | Active
}

public record ObserverHealth(
    string ObserverName,
    bool IsRunning,
    int EventsCollected,
    int PhiScrubCount,
    DateTimeOffset LastActivity);
