using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SuavoAgent.Contracts.Ipc;

namespace SuavoAgent.Helper.Security;

/// <summary>
/// Helper-side <see cref="IFileAccessAttributor"/> that answers "who touched the decoy?" by reading the signed
/// handoff the Broker's ETW oracle writes (<see cref="HoneytokenAttributionStore"/>). Selected when
/// <c>HoneytokenAttributorMode = EtwBroker</c>; otherwise the Null attributor is used.
///
/// Runs SYNCHRONOUSLY on the FileSystemWatcher callback thread (before the reflex dedup lock), so it must be
/// fast and non-blocking: it caches the parsed handoff in-memory and only re-reads when the cache is older
/// than <c>cacheTtl</c> — never disk-per-call. It then does a TIGHT recency-window match between the touch
/// time (now) and the freshest handoff record, so a benign decoy touch can never pair with a LATER unrelated
/// shell-launch record (the only way to manufacture a denylist hit → apoptosis).
///
/// TWO safety properties live here:
///   1. NAME-FORMAT NORMALIZATION — the corroborator's SensitiveDenylist is name-only WITHOUT ".exe"
///      ({powershell, cmd, …}). The handoff carries whatever the resolver produced (often a full path or a
///      ".exe" name). We normalize to <see cref="NormalizeName"/> = filename-without-extension so a REAL
///      powershell touch actually matches the denylist → Apoptosis (without this it would silently Degrade —
///      deaf to its one true positive).
///   2. FAIL-OPEN — empty/stale/wrong-nonce doc, no recency match, any exception → (null, null). Per
///      <see cref="HoneytokenCorroborator"/> an unknown/non-shell toucher is reversible Degrade, never a
///      latched kill — so a read failure can never brick. This attributor adds ZERO escalation logic; the
///      corroborator stays the single never-latch source of truth.
/// </summary>
public sealed class EtwBrokerFileAccessAttributor : IFileAccessAttributor
{
    private readonly string _docPath;
    private readonly string _pipeNonce;
    private readonly TimeSpan _recencyWindow;
    private readonly TimeSpan _forwardSkew;
    private readonly TimeSpan _maxDocAge;
    private readonly TimeSpan _cacheTtl;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<string, string, DateTimeOffset, TimeSpan, IReadOnlyList<HoneytokenAttributionEntry>> _reader;

    private readonly object _sync = new();
    private IReadOnlyList<HoneytokenAttributionEntry> _cache = Array.Empty<HoneytokenAttributionEntry>();
    private DateTimeOffset _cacheStamp = DateTimeOffset.MinValue;

    public EtwBrokerFileAccessAttributor(
        string docPath,
        string pipeNonce,
        Func<DateTimeOffset>? now = null,
        TimeSpan? recencyWindow = null,
        TimeSpan? maxDocAge = null,
        TimeSpan? cacheTtl = null,
        Func<string, string, DateTimeOffset, TimeSpan, IReadOnlyList<HoneytokenAttributionEntry>>? reader = null,
        TimeSpan? forwardSkew = null)
    {
        _docPath = docPath ?? throw new ArgumentNullException(nameof(docPath));
        _pipeNonce = pipeNonce ?? string.Empty;
        _recencyWindow = recencyWindow ?? TimeSpan.FromSeconds(3);
        // Attribution is BACKWARD-looking: a toucher must have been observed at-or-before this touch (up to
        // _recencyWindow ago, for FSW coalescing latency), with only a SMALL forward tolerance for the same
        // access being seen by ETW slightly ahead of the FSW + clock skew. A wide symmetric window would let a
        // LATER, unrelated access's record attribute THIS touch — so the future side is bounded tight.
        _forwardSkew = forwardSkew ?? TimeSpan.FromSeconds(1);
        _maxDocAge = maxDocAge ?? TimeSpan.FromSeconds(10);
        _cacheTtl = cacheTtl ?? TimeSpan.FromMilliseconds(500);
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _reader = reader ?? HoneytokenAttributionStore.Read;
    }

    public FileToucher Attribute(string path)
    {
        try
        {
            lock (_sync)
            {
                var now = _now();
                RefreshIfStale(now);

                // Newest entry observed at-or-before this touch (backward window _recencyWindow) with only a
                // small forward tolerance _forwardSkew (same access seen slightly early by ETW + clock skew).
                HoneytokenAttributionEntry? best = null;
                foreach (var e in _cache)
                {
                    var delta = now - e.ObservedAt;          // >0 = observed in the past
                    if (delta > _recencyWindow) continue;     // too old
                    if (delta < -_forwardSkew) continue;      // too far in the future → a different/later access
                    if (best is null || e.ObservedAt > best.ObservedAt) best = e;
                }

                if (best is null) return new FileToucher(null, null);

                var name = NormalizeName(best.ProcessName) ?? NormalizeName(best.ExePath);
                return new FileToucher(name, best.ExePath);
            }
        }
        catch
        {
            // FAIL-OPEN: a read/parse bug must never brick a pharmacy. Unknown → reversible Degrade.
            return new FileToucher(null, null);
        }
    }

    private void RefreshIfStale(DateTimeOffset now)
    {
        if (now - _cacheStamp <= _cacheTtl) return;
        _cache = _reader(_docPath, _pipeNonce, now, _maxDocAge); // empty list on any failure (fail-open)
        _cacheStamp = now;
    }

    /// <summary>
    /// Reduce a raw resolver output (full path / "x.exe" / bare name) to the filename-without-extension the
    /// corroborator's name-only allow/deny lists compare against. Returns null for null/blank.
    /// </summary>
    internal static string? NormalizeName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim();

        // Split on BOTH separators explicitly: the handoff always carries WINDOWS paths ('\'), but
        // Path.GetFileName treats '\' as a normal char on a non-Windows test/CI host, so a naive call would
        // leave the whole path intact and never match the bare-name denylist (the deafness bug).
        var lastSep = trimmed.LastIndexOfAny(new[] { '\\', '/' });
        var fileName = lastSep >= 0 ? trimmed[(lastSep + 1)..] : trimmed;

        // Strip a single trailing extension (keep internal dots, e.g. "SuavoAgent.Helper").
        var dot = fileName.LastIndexOf('.');
        var name = dot > 0 ? fileName[..dot] : fileName;

        return name.Length == 0 ? null : name.ToLowerInvariant();
    }

    /// <summary>Test seam: current cached toucher count (post-refresh policy not triggered).</summary>
    internal int CachedCount
    {
        get { lock (_sync) return _cache.Count; }
    }
}
