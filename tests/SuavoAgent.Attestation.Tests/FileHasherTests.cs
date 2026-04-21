using SuavoAgent.Attestation;
using Xunit;

namespace SuavoAgent.Attestation.Tests;

public class FileHasherTests
{
    [Fact]
    public void Sha256_KnownContent_ProducesExpectedHash()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"hash-test-{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllText(tmp, "hello world");
            var hash = new FileHasher().Sha256(tmp);
            // Known SHA-256 of "hello world"
            Assert.Equal("b94d27b9934d3e08a52e52d7da7dabfac484efe37a5380ee9088f7ace2efcde9", hash);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void Sha256_MissingFile_ReturnsNull()
    {
        var hash = new FileHasher().Sha256("/nonexistent/path/file.bin");
        Assert.Null(hash);
    }

    [Fact]
    public void Sha256_IsLowercase()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"hash-test-{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllText(tmp, "x");
            var hash = new FileHasher().Sha256(tmp);
            Assert.NotNull(hash);
            Assert.Equal(hash.ToLowerInvariant(), hash);
        }
        finally { File.Delete(tmp); }
    }
}
