using SuavoAgent.Contracts.Behavioral;

namespace SuavoAgent.Core.Ipc;

public sealed class EventRateLimiter
{
    private readonly int _maxPerSecond;
    private int _count;
    private long _windowStart;
    private long _droppedTotal;

    public long DroppedTotal => _droppedTotal;

    public EventRateLimiter(int maxEventsPerSecond = 500)
    {
        _maxPerSecond = maxEventsPerSecond;
        _windowStart = Environment.TickCount64;
    }

    public bool TryAcquire()
    {
        var now = Environment.TickCount64;
        if (now - _windowStart > 1000)
        {
            _count = 0;
            _windowStart = now;
        }

        if (_count >= _maxPerSecond)
        {
            Interlocked.Increment(ref _droppedTotal);
            return false;
        }

        _count++;
        return true;
    }

    public void ResetWindow()
    {
        _count = 0;
        _windowStart = Environment.TickCount64;
    }

    public static List<T> CapBatchSize<T>(List<T> items, int maxBatch)
    {
        return items.Count <= maxBatch ? items : items.Take(maxBatch).ToList();
    }
}
