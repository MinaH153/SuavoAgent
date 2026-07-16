namespace SuavoAgent.Core.Workers;

public sealed partial class RxDetectionWorker
{
    private void OnObservationAuthorityLost(string _) => SuspendObservation();

    private void SuspendObservation()
    {
        var engine = Interlocked.Exchange(ref _sqlEngine, null);
        try { engine?.Dispose(); } catch { }
        _sqlConnected = false;
        _learnedFallbackHealthy = false;
        _activeDetectionSource = "none";
    }
}
