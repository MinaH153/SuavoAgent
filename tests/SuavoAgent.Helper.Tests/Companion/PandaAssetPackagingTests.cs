using System.Buffers.Binary;
using System.Security.Cryptography;
using SuavoAgent.Helper.Companion;
using Xunit;

namespace SuavoAgent.Helper.Tests.Companion;

public sealed class PandaAssetPackagingTests
{
    private const string ApprovedSha256 =
        "3ec67eef55a7f106d4c4afb171b056b0f639898cca01c4e873fc72c417e54128";

    [Fact]
    public void PharmacistPanda_IsEmbeddedAsPng()
    {
        var assembly = typeof(CompanionState).Assembly;
        Assert.Contains(WindowsPandaCompanion.AssetResourceName, assembly.GetManifestResourceNames());

        using var stream = assembly.GetManifestResourceStream(WindowsPandaCompanion.AssetResourceName);
        Assert.NotNull(stream);
        var signature = new byte[8];
        Assert.Equal(signature.Length, stream!.Read(signature));
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, signature);
    }

    [Fact]
    public void PharmacistPanda_IsApprovedRgbaCanvasWithAlphaChannel()
    {
        var assembly = typeof(CompanionState).Assembly;
        Assert.EndsWith(
            "pharmacist-panda-v2.png",
            WindowsPandaCompanion.AssetResourceName,
            StringComparison.Ordinal);
        using var stream = assembly.GetManifestResourceStream(
            WindowsPandaCompanion.AssetResourceName);
        Assert.NotNull(stream);
        Span<byte> header = stackalloc byte[26];
        stream!.ReadExactly(header);

        Assert.Equal("IHDR", System.Text.Encoding.ASCII.GetString(header[12..16]));
        Assert.Equal(1024u, BinaryPrimitives.ReadUInt32BigEndian(header[16..20]));
        Assert.Equal(1536u, BinaryPrimitives.ReadUInt32BigEndian(header[20..24]));
        Assert.Equal(8, header[24]);
        Assert.Equal(6, header[25]); // PNG truecolor with alpha, never baked checkerboard RGB.
    }

    [Fact]
    public void PharmacistPanda_MatchesApprovedProvenanceDigest()
    {
        var assembly = typeof(CompanionState).Assembly;
        using var stream = assembly.GetManifestResourceStream(WindowsPandaCompanion.AssetResourceName);

        Assert.NotNull(stream);
        var digest = Convert.ToHexString(SHA256.HashData(stream!)).ToLowerInvariant();
        Assert.Equal(ApprovedSha256, digest);
    }
}
