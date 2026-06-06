using System.Text.Json;
using SuavoAgent.Contracts.Models;

namespace SuavoAgent.Contracts.Ipc;

/// <summary>
/// The signed-by-ACL, nonce-bound, atomic LocalSystem(Broker-writer) → interactive-user(Helper-reader)
/// handoff that carries WHO touched the honeytoken decoy. Structural mirror of
/// <see cref="IpcPeerAttestationStore"/> (records + static store, camelCase JSON, atomic tmp+Guid+File.Move
/// write, fail-CLOSED read returning the SAFE empty default, same gate ORDER). Lives in Contracts so the
/// Broker (writer) and Helper (reader) share ONE type.
///
/// PHI BOUNDARY ENFORCED IN CODE, not by convention. An ETW Kernel-File session sees EVERY file path on the
/// box (including PHI Rx filenames), and the [OutboundPayload] analyzer does NOT guard this local file — so:
///   (1) <see cref="HoneytokenAttributionEntry"/> has NO raw-path field; a PHI path is structurally
///       unrepresentable. The only location member is <c>TokenId</c> = the constant SHA-256 of the decoy
///       FILENAME (provably non-PHI).
///   (2) <see cref="Write"/> DROPS any entry whose <c>TokenId</c> != <see cref="HoneytokenConstants.ComputeId"/>
///       before serializing — even a caller bug cannot smuggle a non-canonical/path-shaped id into the file.
///
/// Forgery defense is the per-file ACL the Broker applies after the atomic move (the installed ProgramData
/// dir grants INTERACTIVE write, and the pipe nonce is user-readable, so nonce-binding alone does NOT stop a
/// local forger — the ACL is the real barrier; an HMAC verified by a Helper running AS the user buys nothing
/// since the user owns the Helper's key). That ACL lives in the Broker writer; this store is pure I/O.
/// </summary>
public sealed record HoneytokenAttributionEntry(
    string TokenId,
    int ProcessId,
    string? ProcessName,
    string? ExePath,
    DateTimeOffset ObservedAt);

public sealed record HoneytokenAttributionDocument(
    int Version,
    string PipeNonce,
    DateTimeOffset WrittenAt,
    IReadOnlyList<HoneytokenAttributionEntry> Touchers);

public static class HoneytokenAttributionStore
{
    public const int CurrentVersion = 1;
    public const string FileName = "honeytoken-attribution.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public static string GetDefaultPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SuavoAgent",
            FileName);

    /// <summary>
    /// Atomically publish the current decoy touchers. PHI-clamp: any entry whose <c>TokenId</c> is not the
    /// canonical decoy id is dropped before serialization (defense-in-depth — the entry has no path field to
    /// begin with). Single writer (the Broker), last-writer-wins, no locking.
    /// </summary>
    /// <param name="hardenBeforePublish">
    /// Optional: invoked on the TEMP file BEFORE the atomic move so the published file is locked down (ACL'd)
    /// the instant it appears — there is never a window where the destination exists user-writable. FAIL-CLOSED:
    /// if it throws, the temp is deleted and NOTHING is published (the prior handoff, if any, stays), and the
    /// exception propagates so the caller treats this write as failed.
    /// </param>
    public static void Write(
        string path,
        string pipeNonce,
        IReadOnlyList<HoneytokenAttributionEntry> touchers,
        DateTimeOffset now,
        Action<string>? hardenBeforePublish = null)
    {
        var canonicalId = HoneytokenConstants.ComputeId();
        var safe = touchers
            .Where(t => string.Equals(t.TokenId, canonicalId, StringComparison.Ordinal))
            .ToList();

        var doc = new HoneytokenAttributionDocument(
            Version: CurrentVersion,
            PipeNonce: pipeNonce,
            WrittenAt: now,
            Touchers: safe);

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var tmpPath = $"{path}.{Guid.NewGuid():N}.tmp";
        var json = JsonSerializer.Serialize(doc, JsonOptions);

        if (hardenBeforePublish is not null)
        {
            try
            {
                // Create the temp EMPTY, lock its ACL BEFORE any payload lands, THEN write the content into the
                // already-locked temp. (Hardening AFTER writing left a window where the temp held the payload
                // under an inherited, user-writable ACL — Codex.) The temp name is an unguessable GUID, and a
                // racer can at worst make a step fail → fail-closed (nothing published), never a forged publish.
                using (File.Create(tmpPath)) { }
                hardenBeforePublish(tmpPath);
                File.WriteAllText(tmpPath, json);
            }
            catch
            {
                // Fail-closed: an un-hardened (forgeable) doc must NEVER be published.
                try { File.Delete(tmpPath); } catch { /* best-effort */ }
                throw;
            }
        }
        else
        {
            File.WriteAllText(tmpPath, json);
        }

        File.Move(tmpPath, path, overwrite: true);
    }

    /// <summary>
    /// Read the fresh, nonce-matched touchers. FAIL-CLOSED: any failure (missing/torn file, wrong version,
    /// wrong nonce, future-skew, stale) → EMPTY list, never throws. A fresh-but-future-skewed entry is dropped
    /// per-entry. Gate ORDER mirrors <see cref="IpcPeerAttestationStore.ContainsHelper"/>.
    /// </summary>
    public static IReadOnlyList<HoneytokenAttributionEntry> Read(
        string path,
        string pipeNonce,
        DateTimeOffset now,
        TimeSpan maxAge)
    {
        try
        {
            if (!File.Exists(path)) return Array.Empty<HoneytokenAttributionEntry>();

            var doc = JsonSerializer.Deserialize<HoneytokenAttributionDocument>(
                File.ReadAllText(path),
                JsonOptions);
            if (doc is null) return Array.Empty<HoneytokenAttributionEntry>();
            if (doc.Version != CurrentVersion) return Array.Empty<HoneytokenAttributionEntry>();
            if (!string.Equals(doc.PipeNonce, pipeNonce, StringComparison.Ordinal))
                return Array.Empty<HoneytokenAttributionEntry>();
            if (doc.WrittenAt > now.AddMinutes(1)) return Array.Empty<HoneytokenAttributionEntry>();
            if (now - doc.WrittenAt > maxAge) return Array.Empty<HoneytokenAttributionEntry>();

            // Drop any future-skewed entry; keep the rest. A null Touchers list reads as empty.
            return (doc.Touchers ?? (IReadOnlyList<HoneytokenAttributionEntry>)Array.Empty<HoneytokenAttributionEntry>())
                .Where(t => t.ObservedAt <= now.AddMinutes(1))
                .ToList();
        }
        catch
        {
            return Array.Empty<HoneytokenAttributionEntry>();
        }
    }
}
