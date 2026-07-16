using Microsoft.Extensions.Logging.Abstractions;
using PioneerRxSim;
using SuavoAgent.Core.Pricing;
using Xunit;

namespace SuavoAgent.Core.Tests.Pricing;

public sealed class PioneerRxSimTop500WriterTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"pioneerrx-top500-writer-{Guid.NewGuid():N}");

    public PioneerRxSimTop500WriterTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Write_ProducesFiveHundredReadableNdcRows()
    {
        var path = SyntheticTop500XlsxWriter.Write(
            _root,
            new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero));

        var result = new ExcelPricingReader(NullLogger<ExcelPricingReader>.Instance)
            .Read(path, "NDC");

        Assert.True(result.Success, result.Error);
        Assert.Empty(result.Invalid);
        Assert.Equal(500, result.Rows.Count);
        Assert.Equal("01000000001", result.Rows[0].NdcNormalized);
        Assert.Equal("10000000500", result.Rows[^1].NdcNormalized);
        Assert.True(PioneerRxTop500ExportWorkbookValidator.IsExact(
            path,
            new DateOnly(2026, 7, 15)));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
