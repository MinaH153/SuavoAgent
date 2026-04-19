using SuavoAgent.Contracts.Vision;
using SuavoAgent.Helper.Vision;
using Xunit;

namespace SuavoAgent.Helper.Tests.Vision;

public class NullScreenExtractorTests
{
    [Fact]
    public async Task Extract_ReturnsEmptyFrame()
    {
        var ext = new NullScreenExtractor();
        var screen = new ScreenBytes(new byte[] { 1, 2 }, 320, 240, DateTimeOffset.UtcNow);

        var frame = await ext.ExtractAsync(screen, CancellationToken.None);

        Assert.NotNull(frame);
        Assert.Equal(320, frame.Width);
        Assert.Equal(240, frame.Height);
        Assert.Empty(frame.TextRegions);
        Assert.Empty(frame.Elements);
        Assert.Equal("null", frame.ExtractorId);
    }

    [Fact]
    public void IsReady_True()
    {
        Assert.True(new NullScreenExtractor().IsReady);
    }
}
