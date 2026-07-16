namespace SuavoAgent.Contracts.Security;

/// <summary>
/// Process-independent, fail-closed circuit breaker for the narrow window in
/// which Core cannot persist a requested Pause/Stop. The durable control file
/// remains the normal authority. This named event only bridges persistence
/// failure until the current signed lease is deleted or naturally expires.
/// </summary>
public static class ObservationEmergencyStop
{
    private const string EventName = @"Global\SuavoAgent.ObservationEmergencyStop.v1";
    private static readonly object Sync = new();
    private static EventWaitHandle? _processSignal;
    private static int _processLatched;

    public static void Latch()
    {
        Interlocked.Exchange(ref _processLatched, 1);
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            lock (Sync)
            {
                _processSignal ??= new EventWaitHandle(
                    initialState: true,
                    EventResetMode.ManualReset,
                    EventName,
                    out _);
                _processSignal.Set();
            }
        }
        catch
        {
            // The caller also removes the live lease. A failed circuit-breaker
            // publication can never turn a failed Pause into an acknowledgement.
        }
    }

    public static bool IsLatched()
    {
        if (Volatile.Read(ref _processLatched) != 0) return true;
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var signal = EventWaitHandle.OpenExisting(EventName);
            return signal.WaitOne(0);
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }
        catch
        {
            // If another process created the stop signal but this process
            // cannot prove its state, continuing observation would be unsafe.
            return true;
        }
    }
}
