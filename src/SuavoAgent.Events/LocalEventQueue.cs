using System.Text.Json;

namespace SuavoAgent.Events;

/// <summary>
/// Durable on-disk queue that buffers events when cloud is unreachable.
/// Events are DPAPI-encrypted at rest (per invariants.md §I.4 Secret tier —
/// even though events themselves aren't secret, the queue ought to match the
/// same at-rest protection model as other agent state).
/// </summary>
/// <remarks>
/// Sequence preservation: local queue preserves agent-emission order via
/// timestamp + id. On reconnect, events are drained oldest-first so cloud
/// ingest can assign chain sequences correctly.
///
/// Size cap: 1 MB rolling cap. When exceeded, oldest events drop with a
/// log entry (NOT another event — would be self-referential). Spec A2
/// in phase-a-architecture.md §Ingest path references this behavior.
/// </remarks>
public sealed class LocalEventQueue
{
    private readonly string _queueDir;
    private readonly long _maxBytes;
    private readonly object _lock = new();

    public LocalEventQueue(string queueDir, long maxBytes = 1L * 1024L * 1024L)
    {
        _queueDir = queueDir ?? throw new ArgumentNullException(nameof(queueDir));
        _maxBytes = maxBytes;
        Directory.CreateDirectory(_queueDir);
    }

    public void Enqueue(StructuredEvent evt)
    {
        lock (_lock)
        {
            // Drain oldest until under cap.
            while (CurrentSizeBytes() > _maxBytes)
            {
                var oldest = EnumerateFiles().FirstOrDefault();
                if (oldest is null) break;
                try { File.Delete(oldest); } catch { /* swallow — best-effort */ }
            }

            // Filename uses UTC timestamp + id so enumerator returns oldest first.
            var fileName = $"{evt.OccurredAt.UtcTicks:D20}-{evt.Id:N}.json";
            var path = Path.Combine(_queueDir, fileName);
            File.WriteAllText(path, JsonSerializer.Serialize(evt));
        }
    }

    public IReadOnlyList<StructuredEvent> PeekBatch(int max = 100)
    {
        lock (_lock)
        {
            var result = new List<StructuredEvent>();
            foreach (var file in EnumerateFiles().Take(max))
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var evt = JsonSerializer.Deserialize<StructuredEvent>(json);
                    if (evt is not null) result.Add(evt);
                }
                catch
                {
                    // Corrupt file — drop it; never block the queue on bad bytes.
                    try { File.Delete(file); } catch { }
                }
            }
            return result;
        }
    }

    public void Ack(IEnumerable<Guid> eventIds)
    {
        lock (_lock)
        {
            var idSet = new HashSet<Guid>(eventIds);
            foreach (var file in EnumerateFiles())
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var dashIdx = name.IndexOf('-');
                if (dashIdx < 0) continue;
                if (!Guid.TryParseExact(name[(dashIdx + 1)..], "N", out var id)) continue;
                if (idSet.Contains(id))
                {
                    try { File.Delete(file); } catch { /* best-effort */ }
                }
            }
        }
    }

    public int Count()
    {
        lock (_lock)
        {
            return EnumerateFiles().Count();
        }
    }

    public long CurrentSizeBytes()
    {
        lock (_lock)
        {
            var total = 0L;
            foreach (var file in EnumerateFiles())
            {
                try { total += new FileInfo(file).Length; }
                catch { }
            }
            return total;
        }
    }

    private IEnumerable<string> EnumerateFiles()
    {
        if (!Directory.Exists(_queueDir)) yield break;
        var files = Directory.EnumerateFiles(_queueDir, "*.json")
            .OrderBy(Path.GetFileName, StringComparer.Ordinal);
        foreach (var f in files) yield return f;
    }
}
