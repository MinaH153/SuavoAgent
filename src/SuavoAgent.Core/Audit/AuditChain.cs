using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SuavoAgent.Core.Audit;

/// <summary>
/// In-memory, append-only hash-chained audit log for a single pharmacy.
/// Scaffolding only — persistence, S3 Object Lock offsite, and multi-pharmacy
/// isolation land in Phase A item A2. The public API is stable so downstream
/// code can be written against it now.
///
/// Thread safety: <see cref="Append"/> and <see cref="VerifyChain"/> are
/// serialised internally via a simple lock. Contention is expected to be
/// negligible at pilot scale (&lt; 10 events/sec/pharmacy).
/// </summary>
public sealed class AuditChain
{
    /// <summary>
    /// The genesis "previous hash" — the all-zero SHA-256 digest. Every
    /// pharmacy chain starts with this literal string as the first entry's
    /// <c>PreviousEntryHash</c>.
    /// </summary>
    public const string GenesisPreviousHash =
        "0000000000000000000000000000000000000000000000000000000000000000";

    private readonly object _lock = new();
    private readonly List<HashChainedAuditEntry> _entries = new();
    private long _nextSequence;
    private string _lastHash = GenesisPreviousHash;

    /// <summary>
    /// Number of entries currently in the chain. Thread-safe snapshot.
    /// </summary>
    public int Count
    {
        get { lock (_lock) { return _entries.Count; } }
    }

    /// <summary>
    /// Append a new audit entry. Computes the entry hash over the canonical
    /// serialisation of the entry's content plus the previous hash, links the
    /// chain, and returns the populated entry.
    /// </summary>
    public HashChainedAuditEntry Append(
        string eventType,
        string actor,
        string subjectType,
        string subjectId,
        IReadOnlyDictionary<string, object?> metadata)
    {
        if (string.IsNullOrWhiteSpace(eventType))
        {
            throw new ArgumentException("eventType must be non-empty", nameof(eventType));
        }
        if (actor is null)
        {
            throw new ArgumentNullException(nameof(actor));
        }
        if (subjectType is null)
        {
            throw new ArgumentNullException(nameof(subjectType));
        }
        if (subjectId is null)
        {
            throw new ArgumentNullException(nameof(subjectId));
        }
        if (metadata is null)
        {
            throw new ArgumentNullException(nameof(metadata));
        }

        lock (_lock)
        {
            var seq = _nextSequence;
            var entryId = Guid.NewGuid();
            var occurredAt = DateTimeOffset.UtcNow;
            var prev = _lastHash;

            var entryHash = ComputeEntryHash(
                seq, entryId, occurredAt, eventType, actor, subjectType, subjectId, metadata, prev);

            var entry = new HashChainedAuditEntry(
                SequenceNumber: seq,
                EntryId: entryId,
                OccurredAt: occurredAt,
                EventType: eventType,
                Actor: actor,
                SubjectType: subjectType,
                SubjectId: subjectId,
                Metadata: metadata,
                PreviousEntryHash: prev,
                EntryHash: entryHash);

            _entries.Add(entry);
            _nextSequence = seq + 1;
            _lastHash = entryHash;
            return entry;
        }
    }

    /// <summary>
    /// Walk the chain and verify (a) each entry's recomputed hash matches its
    /// stored <c>EntryHash</c>, (b) sequence numbers are monotonic with no
    /// gaps, and (c) each entry's <c>PreviousEntryHash</c> matches the prior
    /// entry's <c>EntryHash</c> (or <see cref="GenesisPreviousHash"/> for the
    /// first entry). Returns <c>true</c> iff all invariants hold.
    /// </summary>
    public bool VerifyChain()
    {
        lock (_lock)
        {
            var expectedPrev = GenesisPreviousHash;
            long expectedSeq = 0;
            foreach (var entry in _entries)
            {
                if (entry.SequenceNumber != expectedSeq)
                {
                    return false;
                }
                if (!StringsEqual(entry.PreviousEntryHash, expectedPrev))
                {
                    return false;
                }

                var recomputed = ComputeEntryHash(
                    entry.SequenceNumber,
                    entry.EntryId,
                    entry.OccurredAt,
                    entry.EventType,
                    entry.Actor,
                    entry.SubjectType,
                    entry.SubjectId,
                    entry.Metadata,
                    entry.PreviousEntryHash);

                if (!StringsEqual(recomputed, entry.EntryHash))
                {
                    return false;
                }

                expectedPrev = entry.EntryHash;
                expectedSeq = entry.SequenceNumber + 1;
            }
            return true;
        }
    }

    /// <summary>
    /// Snapshot of the current chain. Safe to iterate; entries are immutable
    /// records.
    /// </summary>
    public IReadOnlyList<HashChainedAuditEntry> Snapshot()
    {
        lock (_lock)
        {
            return _entries.ToArray();
        }
    }

    // --- Internal: test-seam replacement for tampered-entry verification ---

    /// <summary>
    /// Replace an entry at the given index. Test-only seam used to verify that
    /// tampering is detected by <see cref="VerifyChain"/>. Marked internal so
    /// production code cannot invoke it.
    /// </summary>
    internal void ReplaceEntryForTest(int index, HashChainedAuditEntry replacement)
    {
        lock (_lock)
        {
            if (index < 0 || index >= _entries.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }
            _entries[index] = replacement;
        }
    }

    // --- Canonical serialisation + SHA-256 ---

    private static string ComputeEntryHash(
        long sequenceNumber,
        Guid entryId,
        DateTimeOffset occurredAt,
        string eventType,
        string actor,
        string subjectType,
        string subjectId,
        IReadOnlyDictionary<string, object?> metadata,
        string previousEntryHash)
    {
        // Deterministic, stable representation of the entry's content. We
        // DO NOT feed raw JSON of the record because C# dictionary key order
        // is not guaranteed across runtimes. Instead we build a canonical
        // string by concatenating each field, with metadata serialised
        // via SortedKeyCanonicalJson.
        var canonicalMetadata = SortedKeyCanonicalJson(metadata);

        var payload = new StringBuilder();
        payload.Append(sequenceNumber.ToString(CultureInfo.InvariantCulture));
        payload.Append('|');
        payload.Append(entryId.ToString("D", CultureInfo.InvariantCulture));
        payload.Append('|');
        payload.Append(occurredAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        payload.Append('|');
        payload.Append(eventType);
        payload.Append('|');
        payload.Append(actor);
        payload.Append('|');
        payload.Append(subjectType);
        payload.Append('|');
        payload.Append(subjectId);
        payload.Append('|');
        payload.Append(canonicalMetadata);
        payload.Append('|');
        payload.Append(previousEntryHash);

        var bytes = Encoding.UTF8.GetBytes(payload.ToString());
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Canonical JSON serialiser: sorts keys lexicographically at every
    /// object level and stringifies values stably. Sufficient for the
    /// scaffolding chain; a full RFC 8785 implementation lands in Phase A
    /// alongside the Postgres persistence layer.
    /// </summary>
    internal static string SortedKeyCanonicalJson(IReadOnlyDictionary<string, object?> dict)
    {
        var sb = new StringBuilder();
        WriteValue(sb, dict);
        return sb.ToString();
    }

    private static void WriteValue(StringBuilder sb, object? value)
    {
        switch (value)
        {
            case null:
                sb.Append("null");
                return;

            case string s:
                WriteJsonString(sb, s);
                return;

            case bool b:
                sb.Append(b ? "true" : "false");
                return;

            case IReadOnlyDictionary<string, object?> dict:
                sb.Append('{');
                var keys = dict.Keys.ToArray();
                Array.Sort(keys, StringComparer.Ordinal);
                for (var i = 0; i < keys.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    WriteJsonString(sb, keys[i]);
                    sb.Append(':');
                    WriteValue(sb, dict[keys[i]]);
                }
                sb.Append('}');
                return;

            case System.Collections.IDictionary legacyDict:
                {
                    var kvs = new List<(string k, object? v)>(legacyDict.Count);
                    foreach (System.Collections.DictionaryEntry de in legacyDict)
                    {
                        kvs.Add((Convert.ToString(de.Key, CultureInfo.InvariantCulture) ?? "", de.Value));
                    }
                    kvs.Sort((a, b) => string.CompareOrdinal(a.k, b.k));
                    sb.Append('{');
                    for (var i = 0; i < kvs.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        WriteJsonString(sb, kvs[i].k);
                        sb.Append(':');
                        WriteValue(sb, kvs[i].v);
                    }
                    sb.Append('}');
                    return;
                }

            case System.Collections.IEnumerable enumerable:
                sb.Append('[');
                var first = true;
                foreach (var item in enumerable)
                {
                    if (!first) sb.Append(',');
                    WriteValue(sb, item);
                    first = false;
                }
                sb.Append(']');
                return;

            case DateTimeOffset dto:
                WriteJsonString(sb, dto.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
                return;

            case DateTime dt:
                WriteJsonString(sb, dt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
                return;

            case Guid g:
                WriteJsonString(sb, g.ToString("D", CultureInfo.InvariantCulture));
                return;

            case IFormattable formattable:
                sb.Append(formattable.ToString(null, CultureInfo.InvariantCulture));
                return;

            default:
                // Fallback: ToString under invariant culture wrapped in JSON string escaping.
                WriteJsonString(sb, Convert.ToString(value, CultureInfo.InvariantCulture) ?? "");
                return;
        }
    }

    private static void WriteJsonString(StringBuilder sb, string s)
    {
        // Minimal JSON string escaping. System.Text.Json would also work but
        // introduces allocations per value; this scaffolding path stays allocation-lean.
        sb.Append('"');
        foreach (var ch in s)
        {
            switch (ch)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (ch < 0x20)
                    {
                        sb.Append("\\u");
                        sb.Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(ch);
                    }
                    break;
            }
        }
        sb.Append('"');
    }

    private static bool StringsEqual(string a, string b) =>
        string.Equals(a, b, StringComparison.Ordinal);

    // Kept for discoverability — the spec references canonical-JSON but we
    // deliberately use a dedicated canonicaliser above rather than
    // JsonSerializer because JsonSerializer does not guarantee key order.
    // This private helper left intentionally as a pointer: DO NOT use
    // JsonSerializer.Serialize for hash inputs.
    private static string UnsafeJson(object? value) =>
        JsonSerializer.Serialize(value);
}
