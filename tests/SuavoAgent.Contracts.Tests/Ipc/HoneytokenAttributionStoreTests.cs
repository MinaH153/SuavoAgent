using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Contracts.Models;
using Xunit;

namespace SuavoAgent.Contracts.Tests.Ipc;

/// <summary>
/// The Broker→Helper honeytoken-attribution handoff. Mirrors IpcPeerAttestationStoreTests' temp-dir scaffold.
/// Locks the two safety-critical properties: FAIL-CLOSED reads (any tamper/staleness → empty, never an entry
/// that could pair with a fresh decoy touch) and the PHI-clamp (Write drops any non-canonical TokenId, and the
/// entry has no raw-path field at all — a PHI path is structurally unrepresentable).
/// </summary>
public sealed class HoneytokenAttributionStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"suavo-hny-attrib-{Guid.NewGuid():N}");
    private readonly string _path;
    private static readonly string Canonical = HoneytokenConstants.ComputeId();

    public HoneytokenAttributionStoreTests()
    {
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, HoneytokenAttributionStore.FileName);
    }

    private static HoneytokenAttributionEntry Entry(
        string? name = "powershell", string? exe = @"C:\Windows\System32\powershell.exe",
        DateTimeOffset? observedAt = null, string? tokenId = null)
        => new(tokenId ?? Canonical, 4242, name, exe, observedAt ?? DateTimeOffset.UtcNow);

    [Fact]
    public void RoundTrip_MatchingNonceAndFresh_ReturnsEntry()
    {
        var now = DateTimeOffset.UtcNow;
        HoneytokenAttributionStore.Write(_path, "nonceA", [Entry(observedAt: now)], now);

        var got = HoneytokenAttributionStore.Read(_path, "nonceA", now, TimeSpan.FromSeconds(10));
        Assert.Single(got);
        Assert.Equal("powershell", got[0].ProcessName);
        Assert.Equal(Canonical, got[0].TokenId);
    }

    [Fact]
    public void WrongNonce_ReturnsEmpty()
    {
        var now = DateTimeOffset.UtcNow;
        HoneytokenAttributionStore.Write(_path, "nonceA", [Entry()], now);
        Assert.Empty(HoneytokenAttributionStore.Read(_path, "nonceB", now, TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void StaleDocument_ReturnsEmpty()
    {
        var written = DateTimeOffset.UtcNow;
        HoneytokenAttributionStore.Write(_path, "n", [Entry(observedAt: written)], written);
        // 30s later, maxAge 10s → the whole doc is stale.
        Assert.Empty(HoneytokenAttributionStore.Read(_path, "n", written.AddSeconds(30), TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void FutureSkewedDocument_ReturnsEmpty()
    {
        var now = DateTimeOffset.UtcNow;
        // Document WrittenAt 5 min in the future → rejected wholesale.
        HoneytokenAttributionStore.Write(_path, "n", [Entry()], now.AddMinutes(5));
        Assert.Empty(HoneytokenAttributionStore.Read(_path, "n", now, TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void FutureSkewedEntry_DroppedPerEntry()
    {
        var now = DateTimeOffset.UtcNow;
        // Fresh doc, but the single entry is observed in the future → dropped, doc otherwise valid.
        HoneytokenAttributionStore.Write(_path, "n", [Entry(observedAt: now.AddMinutes(5))], now);
        Assert.Empty(HoneytokenAttributionStore.Read(_path, "n", now, TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void MissingFile_ReturnsEmpty_NeverThrows()
    {
        Assert.Empty(HoneytokenAttributionStore.Read(
            Path.Combine(_dir, "does-not-exist.json"), "n", DateTimeOffset.UtcNow, TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void TornOrGarbageFile_ReturnsEmpty_NeverThrows()
    {
        File.WriteAllText(_path, "{ this is not valid json ");
        Assert.Empty(HoneytokenAttributionStore.Read(_path, "n", DateTimeOffset.UtcNow, TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void Write_DropsEntryWithNonCanonicalTokenId_PhiClamp()
    {
        var now = DateTimeOffset.UtcNow;
        // A forged/path-shaped TokenId (e.g. a leaked PHI filename) must NEVER be serialized.
        const string forged = @"\Device\HarddiskVolume3\PHI\patient-John-Doe-Rx.pdf";
        HoneytokenAttributionStore.Write(_path, "n",
        [
            Entry(tokenId: forged, name: "evil"),
            Entry(name: "powershell"), // canonical — kept
        ], now);

        // The forged id must be absent from the raw file (no PHI path on disk).
        var raw = File.ReadAllText(_path);
        Assert.DoesNotContain("patient-John-Doe", raw);
        Assert.DoesNotContain("HarddiskVolume3", raw);

        // And Read returns only the canonical entry.
        var got = HoneytokenAttributionStore.Read(_path, "n", now, TimeSpan.FromSeconds(10));
        Assert.Single(got);
        Assert.Equal("powershell", got[0].ProcessName);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }
}
