using Microsoft.Extensions.Options;
using Serilog;
using SuavoAgent.Contracts.Vision;
using SuavoAgent.Core.Config;
using SuavoAgent.Helper.Vision;
using Xunit;

namespace SuavoAgent.Helper.Tests.Vision;

/// <summary>
/// On macOS these tests run without DPAPI — the store falls back to
/// plaintext and still round-trips correctly. On Windows the payload is
/// DPAPI-encrypted at rest but the store's contract is identical.
/// </summary>
public class EncryptedScreenStoreTests
{
    [Fact]
    public async Task StoreAndLoad_RoundTripsBytes()
    {
        using var dir = new TempDir();
        var store = NewStore(dir.Path);
        var original = Bytes(new byte[] { 1, 2, 3, 4, 5 }, 640, 480);

        var id = await store.StoreAsync(original, CancellationToken.None);
        Assert.NotNull(id);

        var loaded = await store.LoadAsync(id!, CancellationToken.None);
        Assert.NotNull(loaded);
        Assert.Equal(640, loaded.Value.Width);
        Assert.Equal(480, loaded.Value.Height);
        Assert.Equal(original.Png, loaded.Value.Png);
    }

    [Fact]
    public async Task Load_UnknownId_ReturnsNull()
    {
        using var dir = new TempDir();
        var store = NewStore(dir.Path);
        var loaded = await store.LoadAsync("does-not-exist", CancellationToken.None);
        Assert.Null(loaded);
    }

    [Fact]
    public async Task Purge_TtlExpired_RemovesOldFiles()
    {
        using var dir = new TempDir();
        var store = NewStore(dir.Path, retentionHours: 1);

        // Write one file and backdate its CreationTime to 2 hours ago.
        var id = await store.StoreAsync(Bytes(new byte[] { 1 }), CancellationToken.None);
        Assert.NotNull(id);
        var file = Path.Combine(dir.Path, $"{id}.scn");
        File.SetCreationTimeUtc(file, DateTime.UtcNow.AddHours(-2));

        var removed = await store.PurgeExpiredAsync(CancellationToken.None);
        Assert.True(removed >= 1);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task Purge_CapExceeded_RemovesOldestFirst()
    {
        using var dir = new TempDir();
        var store = NewStore(dir.Path, maxStoredScreens: 2);

        // Create three files with distinct creation times.
        var ids = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var id = await store.StoreAsync(Bytes(new byte[] { (byte)i }), CancellationToken.None);
            Assert.NotNull(id);
            ids.Add(id!);
            var path = Path.Combine(dir.Path, $"{id}.scn");
            // Ensure CreationTime ordering is deterministic.
            File.SetCreationTimeUtc(path, DateTime.UtcNow.AddSeconds(i));
        }

        await store.PurgeExpiredAsync(CancellationToken.None);

        // Only 2 should remain. Oldest-first (ids[0]) gone.
        Assert.False(File.Exists(Path.Combine(dir.Path, $"{ids[0]}.scn")));
        Assert.True(File.Exists(Path.Combine(dir.Path, $"{ids[1]}.scn")));
        Assert.True(File.Exists(Path.Combine(dir.Path, $"{ids[2]}.scn")));
    }

    [Fact]
    public async Task Store_WithZeroBytesOk()
    {
        // Degenerate case — empty PNG. Should still round-trip.
        using var dir = new TempDir();
        var store = NewStore(dir.Path);
        var id = await store.StoreAsync(Bytes(Array.Empty<byte>()), CancellationToken.None);
        Assert.NotNull(id);

        var loaded = await store.LoadAsync(id!, CancellationToken.None);
        Assert.NotNull(loaded);
        Assert.Empty(loaded.Value.Png);
    }

    // --- helpers -------------------------------------------------------------

    private static EncryptedScreenStore NewStore(
        string dir, int retentionHours = 24, int maxStoredScreens = 500)
    {
        var opts = new AgentOptions
        {
            Vision = new VisionOptions
            {
                Enabled = true,
                StorageDirectory = dir,
                RetentionHours = retentionHours,
                MaxStoredScreens = maxStoredScreens,
            },
        };
        return new EncryptedScreenStore(
            Options.Create(opts),
            new LoggerConfiguration().CreateLogger());
    }

    private static ScreenBytes Bytes(byte[] png, int w = 100, int h = 100) =>
        new(png, w, h, DateTimeOffset.UtcNow);

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }
        public TempDir()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "suavo-screen-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { }
        }
    }
}
