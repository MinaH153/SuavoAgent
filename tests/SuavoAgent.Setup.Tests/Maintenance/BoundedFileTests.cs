using System.Security.Cryptography;
using SuavoAgent.Setup.Maintenance;
using Xunit;

namespace SuavoAgent.Setup.Tests.Maintenance;

public sealed class BoundedFileTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "suavo-bounded-file-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Read_rejects_file_larger_than_bound()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "oversized.bin");
        File.WriteAllBytes(path, new byte[1025]);

        Assert.Throws<InvalidDataException>(() => BoundedFile.ReadBytes(path, 1024));
    }

    [Fact]
    public void Copy_rejects_oversized_source_before_creating_destination()
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "source.bin");
        var destination = Path.Combine(_root, "destination.bin");
        var bytes = new byte[1025];
        File.WriteAllBytes(source, bytes);

        Assert.Throws<InvalidDataException>(() => BoundedFile.CopyAndHashVerify(
            source,
            destination,
            1024,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()));
        Assert.False(File.Exists(destination));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
        catch { }
    }
}
