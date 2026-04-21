namespace SuavoAgent.Verbs;

/// <summary>
/// Thin abstraction over sc.exe used by infrastructure verbs. Mirrors
/// <c>SuavoAgent.Watchdog.IServiceCommand</c> but is re-stated here to keep
/// Verbs independent of Watchdog.
/// </summary>
public interface IServiceController
{
    ServiceState Query(string serviceName);
    bool Start(string serviceName, TimeSpan timeout);
    bool Stop(string serviceName, TimeSpan timeout);
}

public enum ServiceState
{
    Unknown,
    Running,
    Stopped,
    StartPending,
    StopPending,
    NotInstalled
}
