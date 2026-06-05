using System;
using System.IO;
using System.Text.Json;

namespace SuavoAgent.Core.Cloud;

/// <summary>Persisted probation record for an OTA set on trial.</summary>
public sealed record OtaProbationRecord(string Version, DateTimeOffset SwappedAtUtc, int BootsSinceSwap);

/// <summary>
/// Durable state for the OTA crash-loop guard: a single JSON flag in the install dir that marks an
/// OTA set as "on trial" and counts un-healthy boots. Writes are atomic (temp + replace) so a crash
/// mid-write can't corrupt the flag, and the boot counter is incremented + persisted EARLY in startup
/// (before risky init) so a crash-loop converges. The pure decision lives in
/// <see cref="OtaProbationEvaluator"/>; this is only the I/O.
/// </summary>
public sealed class OtaProbationStore
{
    public const string FileName = "ota-probation.json";

    private readonly string _path;

    public OtaProbationStore(string installDir)
    {
        if (string.IsNullOrEmpty(installDir)) throw new ArgumentException("installDir required", nameof(installDir));
        _path = Path.Combine(installDir, FileName);
    }

    public bool HasProbation => File.Exists(_path);

    public OtaProbationRecord? Read()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<OtaProbationRecord>(File.ReadAllText(_path))
                : null;
        }
        catch (Exception)
        {
            return null; // unreadable/corrupt flag ⇒ treat as no probation (fail-open to normal startup)
        }
    }

    /// <summary>Begin probation for a freshly-swapped set (boot count starts at 0). Atomic.</summary>
    public void Begin(string version, DateTimeOffset swappedAtUtc) =>
        WriteAtomic(new OtaProbationRecord(version, swappedAtUtc, BootsSinceSwap: 0));

    /// <summary>
    /// Increment + persist the un-healthy boot count, returning the new value. Throws if there is no
    /// record or the write fails — the caller treats that as fatal/escalated (never silently continues),
    /// so a lost count can't defeat crash-loop convergence.
    /// </summary>
    public int IncrementBoots()
    {
        var rec = Read() ?? throw new InvalidOperationException("IncrementBoots called with no probation record");
        var bumped = rec with { BootsSinceSwap = rec.BootsSinceSwap + 1 };
        WriteAtomic(bumped);
        return bumped.BootsSinceSwap;
    }

    /// <summary>Probation resolved (committed or downgraded) — remove the flag.</summary>
    public void Clear()
    {
        try { if (File.Exists(_path)) File.Delete(_path); }
        catch (Exception) { /* best-effort; a lingering flag is reconciled by the stale-TTL path */ }
    }

    private void WriteAtomic(OtaProbationRecord rec)
    {
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(rec));
        File.Move(tmp, _path, overwrite: true);
    }
}
