using System.Text;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Diagnostics;
using Xunit;

namespace SuavoAgent.Core.Tests.Cloud;

/// <summary>
/// Disk-persistence roundtrip + atomic-replace + orphan-sweep coverage for
/// <see cref="RulesetSyncStore"/> (Codex Comp 2 chunk D HIGH + chunk B
/// MEDIUM RESOLVED).
/// </summary>
public sealed class RulesetSyncStoreTests : IDisposable
{
    private readonly string _tempDir;

    public RulesetSyncStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(),
            $"ruleset-sync-store-tests-{Guid.NewGuid():N}");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch { /* swallow — test teardown best-effort */ }
    }

    private static RulesetBundle MakeBundle(string version = "v1.0", int versionInt = 10)
    {
        var rs = new RulesetV1
        {
            RulesetVersion = version,
            RulesetVersionInt = versionInt,
            KeyId = "test-key",
            SignedAt = "2026-05-14T00:00:00Z",
            SignatureAlg = "ECDSA_P256_SHA256",
        };
        return new RulesetBundle(rs, Encoding.UTF8.GetBytes("sig-bytes"), ETag: "etag-1");
    }

    [Fact]
    public async Task InitializeAsync_creates_directory_when_missing()
    {
        Assert.False(Directory.Exists(_tempDir));

        var store = new RulesetSyncStore(_tempDir);
        await store.InitializeAsync();

        Assert.True(Directory.Exists(_tempDir));
    }

    [Fact]
    public async Task SaveAsync_first_install_writes_current_file()
    {
        var store = new RulesetSyncStore(_tempDir);
        await store.InitializeAsync();
        var bundle = MakeBundle();

        await store.SaveAsync(bundle);

        Assert.True(File.Exists(store.CurrentPath));
        Assert.False(File.Exists(store.PreviousPath));  // no backup on first install
    }

    [Fact]
    public async Task SaveAsync_second_install_writes_previous_backup()
    {
        var store = new RulesetSyncStore(_tempDir);
        await store.InitializeAsync();
        await store.SaveAsync(MakeBundle("v1.0", 10));

        var newBundle = MakeBundle("v1.1", 11);
        await store.SaveAsync(newBundle);

        Assert.True(File.Exists(store.CurrentPath));
        Assert.True(File.Exists(store.PreviousPath));  // backup populated
        Assert.Equal("v1.1", store.GetCurrentVersion());
        Assert.Equal(11, store.GetCurrentVersionInt());
    }

    [Fact]
    public async Task TryLoadCurrent_returns_bundle_roundtripped_through_disk()
    {
        var store = new RulesetSyncStore(_tempDir);
        await store.InitializeAsync();
        var original = MakeBundle("v1.5", 15);

        await store.SaveAsync(original);
        var loaded = store.TryLoadCurrent();

        Assert.NotNull(loaded);
        Assert.Equal("v1.5", loaded!.Ruleset.RulesetVersion);
        Assert.Equal(15, loaded.Ruleset.RulesetVersionInt);
        Assert.Equal("test-key", loaded.Ruleset.KeyId);
        Assert.Equal(Encoding.UTF8.GetBytes("sig-bytes"), loaded.SignatureBytes);
        Assert.Equal("etag-1", loaded.ETag);
    }

    [Fact]
    public void TryLoadCurrent_returns_null_when_no_file()
    {
        Directory.CreateDirectory(_tempDir);
        var store = new RulesetSyncStore(_tempDir);

        Assert.Null(store.TryLoadCurrent());
        Assert.Null(store.GetCurrentVersion());
        Assert.Equal(0, store.GetCurrentVersionInt());
    }

    [Fact]
    public async Task TryLoadCurrent_returns_null_on_malformed_json()
    {
        var store = new RulesetSyncStore(_tempDir);
        await store.InitializeAsync();

        await File.WriteAllTextAsync(store.CurrentPath, "{ not valid json");

        Assert.Null(store.TryLoadCurrent());
    }

    [Fact]
    public async Task InitializeAsync_sweeps_old_orphan_temp_files()
    {
        Directory.CreateDirectory(_tempDir);

        // Old orphan — write a temp file and backdate its LastWriteTime.
        var oldTemp = Path.Combine(_tempDir, $"{RulesetSyncStore.TempPrefix}old{RulesetSyncStore.TempSuffix}");
        await File.WriteAllTextAsync(oldTemp, "leftover from crashed save");
        File.SetLastWriteTimeUtc(oldTemp, DateTime.UtcNow - TimeSpan.FromHours(2));

        // Fresh temp — should NOT be swept (sibling process may be writing).
        var youngTemp = Path.Combine(_tempDir, $"{RulesetSyncStore.TempPrefix}young{RulesetSyncStore.TempSuffix}");
        await File.WriteAllTextAsync(youngTemp, "in flight");

        // Unrelated file — never touched.
        var unrelated = Path.Combine(_tempDir, "some-other.json");
        await File.WriteAllTextAsync(unrelated, "{}");

        var store = new RulesetSyncStore(_tempDir);
        await store.InitializeAsync();

        Assert.False(File.Exists(oldTemp));    // swept
        Assert.True(File.Exists(youngTemp));   // preserved
        Assert.True(File.Exists(unrelated));   // preserved
    }

    [Fact]
    public async Task SaveAsync_cancellation_is_propagated()
    {
        var store = new RulesetSyncStore(_tempDir);
        await store.InitializeAsync();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<TaskCanceledException>(
            () => store.SaveAsync(MakeBundle(), cts.Token));
    }

    [Fact]
    public void Constructor_with_null_directory_throws()
    {
        Assert.Throws<ArgumentNullException>(() => new RulesetSyncStore(directory: null!));
    }
}
