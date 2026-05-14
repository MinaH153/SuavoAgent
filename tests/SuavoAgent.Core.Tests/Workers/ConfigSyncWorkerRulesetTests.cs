using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Contracts.Cloud;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Workers;
using SuavoAgent.Diagnostics;
using Xunit;

namespace SuavoAgent.Core.Tests.Workers;

/// <summary>
/// 7-scenario failure-mode matrix from Comp 2 design §B for the OTA
/// pipeline inside <see cref="ConfigSyncWorker"/>.
/// </summary>
/// <remarks>
/// Calls the worker's <c>internal</c> <c>PollRulesetAsync</c> +
/// <c>InitializeRulesetCacheAsync</c> directly via <c>InternalsVisibleTo</c>
/// so the full ExecuteAsync loop isn't required for each assertion. The
/// config-side of the worker is unaffected — its existing tests still pass.
/// </remarks>
public sealed class ConfigSyncWorkerRulesetTests : IDisposable
{
    private readonly string _tempDir;

    public ConfigSyncWorkerRulesetTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(),
            $"config-sync-worker-ruleset-{Guid.NewGuid():N}");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch { /* swallow */ }
    }

    private static RulesetV1 MakeRuleset(int versionInt = 11, string keyId = "test-key")
    {
        return new RulesetV1
        {
            RulesetVersion = $"v1.{versionInt}",
            RulesetVersionInt = versionInt,
            KeyId = keyId,
            SignedAt = "2026-05-14T00:00:00Z",
            SignatureAlg = "ECDSA_P256_SHA256",
        };
    }

    private (ConfigSyncWorker Worker,
             FakeRulesetClient Client,
             RulesetSyncStore Store,
             FakeVerifier Verifier,
             FakeSwapper Swapper) BuildWorker(int initialAgentInt = 10)
    {
        var client = new FakeRulesetClient();
        var store = new RulesetSyncStore(_tempDir);
        var verifier = new FakeVerifier();
        var swapper = new FakeSwapper(initialAgentInt);
        var worker = new ConfigSyncWorker(
            client: new NoopConfigClient(),
            store: NewConfigOverrideStore(),
            opts: new ConfigSyncOptions(),
            logger: NullLogger<ConfigSyncWorker>.Instance,
            rulesetClient: client,
            rulesetStore: store,
            rulesetVerifier: verifier,
            rulesetSwapper: swapper);
        return (worker, client, store, verifier, swapper);
    }

    private ConfigOverrideStore NewConfigOverrideStore()
    {
        var path = Path.Combine(_tempDir, "config-overrides.json");
        Directory.CreateDirectory(_tempDir);
        return new ConfigOverrideStore(path, NullLogger<ConfigOverrideStore>.Instance);
    }

    // ── Scenario 1: Updated — happy path ─────────────────────────────────

    [Fact]
    public async Task PollRulesetAsync_with_Updated_and_valid_signature_swaps_and_persists()
    {
        var (worker, client, store, verifier, swapper) = BuildWorker(initialAgentInt: 10);
        var bundle = new RulesetBundle(MakeRuleset(11), new byte[] { 1, 2, 3 }, ETag: "\"etag-A\"");
        client.NextResult = new RulesetFetchResult(bundle, RulesetFetchOutcome.Updated);
        verifier.NextResult = RulesetVerificationResult.Ok();

        await worker.PollRulesetAsync(CancellationToken.None);

        Assert.True(File.Exists(store.CurrentPath));
        Assert.Equal(1, swapper.SwapCount);
        Assert.Equal(11, swapper.LastSwappedVersionInt);
    }

    // ── Scenario 2: NotModified — no-op ──────────────────────────────────

    [Fact]
    public async Task PollRulesetAsync_with_NotModified_does_not_swap_or_persist()
    {
        var (worker, client, store, _, swapper) = BuildWorker();
        client.NextResult = new RulesetFetchResult(null, RulesetFetchOutcome.NotModified);

        await worker.PollRulesetAsync(CancellationToken.None);

        Assert.False(File.Exists(store.CurrentPath));
        Assert.Equal(0, swapper.SwapCount);
    }

    // ── Scenario 3: AuthFailed — alarm + preserve cache ──────────────────

    [Fact]
    public async Task PollRulesetAsync_with_AuthFailed_does_not_swap_or_persist()
    {
        var (worker, client, store, _, swapper) = BuildWorker();
        client.NextResult = new RulesetFetchResult(null, RulesetFetchOutcome.AuthFailed);
        client.LastFailureKindOverride = "http_401_unauthorized";

        await worker.PollRulesetAsync(CancellationToken.None);

        Assert.False(File.Exists(store.CurrentPath));
        Assert.Equal(0, swapper.SwapCount);
    }

    // ── Scenario 4: Transient — no alarm, preserve cache ─────────────────

    [Fact]
    public async Task PollRulesetAsync_with_Transient_does_not_swap_or_persist()
    {
        var (worker, client, store, _, swapper) = BuildWorker();
        client.NextResult = new RulesetFetchResult(null, RulesetFetchOutcome.Transient);

        await worker.PollRulesetAsync(CancellationToken.None);

        Assert.False(File.Exists(store.CurrentPath));
        Assert.Equal(0, swapper.SwapCount);
    }

    // ── Scenario 5: SchemaInvalid — alarm + preserve cache ───────────────

    [Fact]
    public async Task PollRulesetAsync_with_SchemaInvalid_does_not_swap_or_persist()
    {
        var (worker, client, store, _, swapper) = BuildWorker();
        client.NextResult = new RulesetFetchResult(null, RulesetFetchOutcome.SchemaInvalid);

        await worker.PollRulesetAsync(CancellationToken.None);

        Assert.False(File.Exists(store.CurrentPath));
        Assert.Equal(0, swapper.SwapCount);
    }

    // ── Scenario 6: Signature INVALID — alarm + preserve cache ───────────

    [Fact]
    public async Task PollRulesetAsync_with_signature_INVALID_does_not_swap_or_persist()
    {
        var (worker, client, store, verifier, swapper) = BuildWorker();
        var bundle = new RulesetBundle(MakeRuleset(11), new byte[] { 1, 2, 3 }, ETag: "");
        client.NextResult = new RulesetFetchResult(bundle, RulesetFetchOutcome.Updated);
        verifier.NextResult = RulesetVerificationResult.Fail("Signature mismatch");

        await worker.PollRulesetAsync(CancellationToken.None);

        Assert.False(File.Exists(store.CurrentPath));
        Assert.Equal(0, swapper.SwapCount);
    }

    [Fact]
    public async Task PollRulesetAsync_with_key_id_mismatch_does_not_swap()
    {
        var (worker, client, _, verifier, swapper) = BuildWorker();
        var bundle = new RulesetBundle(MakeRuleset(11, "rotated-out-key"), new byte[] { 1 }, ETag: "");
        client.NextResult = new RulesetFetchResult(bundle, RulesetFetchOutcome.Updated);
        verifier.NextResult = RulesetVerificationResult.Fail("Unknown key_id: rotated-out-key");

        await worker.PollRulesetAsync(CancellationToken.None);

        Assert.Equal(0, swapper.SwapCount);
    }

    [Fact]
    public async Task PollRulesetAsync_with_expired_ruleset_does_not_swap()
    {
        var (worker, client, _, verifier, swapper) = BuildWorker();
        var bundle = new RulesetBundle(MakeRuleset(11), new byte[] { 1 }, ETag: "");
        client.NextResult = new RulesetFetchResult(bundle, RulesetFetchOutcome.Updated);
        verifier.NextResult = RulesetVerificationResult.Fail("Ruleset expired");

        await worker.PollRulesetAsync(CancellationToken.None);

        Assert.Equal(0, swapper.SwapCount);
    }

    // ── Scenario 7: Disk write fail — swap STILL happens ─────────────────

    [Fact]
    public async Task PollRulesetAsync_with_disk_write_fail_still_swaps_in_memory()
    {
        var (worker, client, _, verifier, swapper) = BuildWorker(initialAgentInt: 10);
        var bundle = new RulesetBundle(MakeRuleset(11), new byte[] { 1 }, ETag: "\"etag-X\"");
        client.NextResult = new RulesetFetchResult(bundle, RulesetFetchOutcome.Updated);
        verifier.NextResult = RulesetVerificationResult.Ok();

        // Make the store directory non-writable by pointing at a path that
        // cannot exist as a directory (an existing FILE blocks dir creation).
        var blockerFile = Path.Combine(_tempDir, "blocker");
        Directory.CreateDirectory(_tempDir);
        await File.WriteAllTextAsync(blockerFile, "");
        var storeOnFile = new RulesetSyncStore(Path.Combine(blockerFile, "subdir"));
        // Rebuild the worker pointing at the broken store.
        var brokenWorker = new ConfigSyncWorker(
            client: new NoopConfigClient(),
            store: NewConfigOverrideStore(),
            opts: new ConfigSyncOptions(),
            logger: NullLogger<ConfigSyncWorker>.Instance,
            rulesetClient: client,
            rulesetStore: storeOnFile,
            rulesetVerifier: verifier,
            rulesetSwapper: swapper);

        await brokenWorker.PollRulesetAsync(CancellationToken.None);

        // Disk save failed (no subdir possible under a file), but the
        // in-memory swap STILL fired — that's the round-7 contract.
        Assert.Equal(1, swapper.SwapCount);
        Assert.Equal(11, swapper.LastSwappedVersionInt);
    }

    // ── OTA-time freshness gate ─────────────────────────────────────────

    [Fact]
    public async Task PollRulesetAsync_with_cloud_int_LE_agent_int_rejects_and_does_not_swap()
    {
        var (worker, client, store, verifier, swapper) = BuildWorker(initialAgentInt: 20);
        var staleBundle = new RulesetBundle(MakeRuleset(15), new byte[] { 1 }, ETag: "");
        client.NextResult = new RulesetFetchResult(staleBundle, RulesetFetchOutcome.Updated);
        verifier.NextResult = RulesetVerificationResult.Ok();

        await worker.PollRulesetAsync(CancellationToken.None);

        Assert.False(File.Exists(store.CurrentPath));
        Assert.Equal(0, swapper.SwapCount);
    }

    [Fact]
    public async Task PollRulesetAsync_with_cloud_int_EQ_agent_int_rejects()
    {
        var (worker, client, _, verifier, swapper) = BuildWorker(initialAgentInt: 11);
        var tieBundle = new RulesetBundle(MakeRuleset(11), new byte[] { 1 }, ETag: "");
        client.NextResult = new RulesetFetchResult(tieBundle, RulesetFetchOutcome.Updated);
        verifier.NextResult = RulesetVerificationResult.Ok();

        await worker.PollRulesetAsync(CancellationToken.None);

        // Cloud must be STRICTLY greater than agent — tie rejected.
        Assert.Equal(0, swapper.SwapCount);
    }

    // ── Cache-vs-embedded boot logic ────────────────────────────────────

    [Fact]
    public async Task InitializeRulesetCacheAsync_with_cache_int_GT_embedded_adopts_cache()
    {
        var (worker, _, store, verifier, swapper) = BuildWorker(initialAgentInt: 10);

        // Pre-populate the cache with a NEWER ruleset.
        verifier.NextResult = RulesetVerificationResult.Ok();
        await store.InitializeAsync();
        await store.SaveAsync(new RulesetBundle(MakeRuleset(15), new byte[] { 1 }, ETag: "\"cached-etag\""));

        await worker.InitializeRulesetCacheAsync(CancellationToken.None);

        Assert.Equal(1, swapper.SwapCount);
        Assert.Equal(15, swapper.LastSwappedVersionInt);
    }

    [Fact]
    public async Task InitializeRulesetCacheAsync_with_cache_int_EQ_embedded_keeps_embedded()
    {
        var (worker, _, store, verifier, swapper) = BuildWorker(initialAgentInt: 11);
        verifier.NextResult = RulesetVerificationResult.Ok();
        await store.InitializeAsync();
        await store.SaveAsync(new RulesetBundle(MakeRuleset(11), new byte[] { 1 }, ETag: ""));

        await worker.InitializeRulesetCacheAsync(CancellationToken.None);

        Assert.Equal(0, swapper.SwapCount);
    }

    [Fact]
    public async Task InitializeRulesetCacheAsync_with_cached_verify_fail_keeps_embedded()
    {
        var (worker, _, store, verifier, swapper) = BuildWorker(initialAgentInt: 10);
        verifier.NextResult = RulesetVerificationResult.Fail("Unknown key_id: rotated-out");
        await store.InitializeAsync();
        await store.SaveAsync(new RulesetBundle(MakeRuleset(15, "rotated-out"), new byte[] { 1 }, ETag: ""));

        await worker.InitializeRulesetCacheAsync(CancellationToken.None);

        Assert.Equal(0, swapper.SwapCount);
    }

    [Fact]
    public async Task InitializeRulesetCacheAsync_with_no_cache_keeps_embedded()
    {
        var (worker, _, _, _, swapper) = BuildWorker(initialAgentInt: 10);

        await worker.InitializeRulesetCacheAsync(CancellationToken.None);

        Assert.Equal(0, swapper.SwapCount);
    }

    // ── OTA disabled when deps not wired ────────────────────────────────

    [Fact]
    public async Task PollRulesetAsync_when_RulesetOtaDisabled_is_silent_noop()
    {
        // Worker constructed without any ruleset deps.
        var worker = new ConfigSyncWorker(
            client: new NoopConfigClient(),
            store: NewConfigOverrideStore(),
            opts: new ConfigSyncOptions(),
            logger: NullLogger<ConfigSyncWorker>.Instance);

        Assert.False(worker.RulesetOtaEnabled);
        await worker.PollRulesetAsync(CancellationToken.None);  // no throw
        await worker.InitializeRulesetCacheAsync(CancellationToken.None);
    }

    // ── Fakes ────────────────────────────────────────────────────────────

    private sealed class FakeRulesetClient : IRulesetClient
    {
        public RulesetFetchResult NextResult { get; set; }
            = new(null, RulesetFetchOutcome.Transient);
        public string? LastFailureKindOverride { get; set; }
        public string? LastFailureKind => LastFailureKindOverride;
        public Task<RulesetFetchResult> FetchAsync(string? currentETag, CancellationToken ct)
            => Task.FromResult(NextResult);
    }

    private sealed class FakeVerifier : IRulesetVerifier
    {
        public RulesetVerificationResult NextResult { get; set; } = RulesetVerificationResult.Ok();
        public int VerifyCount { get; private set; }
        public RulesetVerificationResult Verify(RulesetBundle bundle)
        {
            VerifyCount++;
            return NextResult;
        }
    }

    private sealed class FakeSwapper : IRulesetSwapper
    {
        public FakeSwapper(int initialVersionInt) { CurrentVersionInt = initialVersionInt; }
        public int CurrentVersionInt { get; private set; }
        public int SwapCount { get; private set; }
        public int LastSwappedVersionInt { get; private set; }
        public void Swap(RulesetV1 ruleset)
        {
            SwapCount++;
            LastSwappedVersionInt = ruleset.RulesetVersionInt;
            CurrentVersionInt = ruleset.RulesetVersionInt;
        }
    }

    private sealed class NoopConfigClient : IAgentConfigClient
    {
        public string? LastFailureKind => null;
        public Task<ConfigOverrideResponse?> FetchAsync(CancellationToken ct)
            => Task.FromResult<ConfigOverrideResponse?>(null);
    }
}
