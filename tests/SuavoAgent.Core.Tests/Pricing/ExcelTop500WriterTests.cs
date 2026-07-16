using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Core.Pricing;
using Xunit;

namespace SuavoAgent.Core.Tests.Pricing;

/// <summary>
/// Proves the generated top-dispensed worklist feeds the pricing loop: what
/// <see cref="ExcelTop500Writer"/> writes, <see cref="ExcelPricingReader"/> can read —
/// so generate → price → write-back chains end to end without the manual export.
/// </summary>
public class ExcelTop500WriterTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"suavo_top500_{Guid.NewGuid():N}");

    public ExcelTop500WriterTests() => Directory.CreateDirectory(_tempDir);

    [Fact]
    public void Written_worklist_is_readable_by_the_pricing_reader()
    {
        // Nadim's real top-3, as the generator would emit them (ranked by dispensed).
        var rows = new List<TopDispensedRow>
        {
            new("Fluticasone Prop 50 Mcg Spray", "50 mcg/actuation", "60505082901", 1523m),
            new("Omeprazole Dr 20 Mg Capsule", "20 mg", "59651000205", 1405m),
            new("Atorvastatin 40 Mg Tablet", "40 mg", "60505258008", 1300m),
        };
        var path = Path.Combine(_tempDir, "top500.xlsx");

        var writer = new ExcelTop500Writer(NullLogger<ExcelTop500Writer>.Instance);
        Assert.True(writer.Write(path, rows));
        PricingWorkbookContentPolicy.Validate(path);

        // The pricing loop's own reader must consume it: find the NDC column + every NDC.
        var reader = new ExcelPricingReader(NullLogger<ExcelPricingReader>.Instance);
        var result = reader.Read(path, "NDC", quantityColumnHint: "Total Dispensed");

        Assert.True(result.Success, result.Error);
        Assert.Equal(3, result.Rows.Count);
        Assert.Equal("60505082901", result.Rows[0].NdcNormalized);
        Assert.Equal("59651000205", result.Rows[1].NdcNormalized);
        Assert.Equal("60505258008", result.Rows[2].NdcNormalized);
        Assert.Equal(1523m, result.Rows[0].Quantity); // Total Dispensed round-trips
    }

    [Fact]
    public void Ndc_is_written_as_text_so_leading_zeros_survive()
    {
        var rows = new List<TopDispensedRow>
        {
            new("Metformin 500 Mg Tablet", "500 mg", "00093512401", 900m), // leading zeros
        };
        var path = Path.Combine(_tempDir, "lz.xlsx");
        Assert.True(new ExcelTop500Writer(NullLogger<ExcelTop500Writer>.Instance).Write(path, rows));

        var result = new ExcelPricingReader(NullLogger<ExcelPricingReader>.Instance).Read(path, "NDC");
        Assert.True(result.Success, result.Error);
        Assert.Single(result.Rows);
        Assert.Equal("00093512401", result.Rows[0].NdcNormalized);
    }

    [Fact]
    public void Atomic_publication_is_readable_and_never_overwrites_command_output()
    {
        var first = new[]
        {
            new TopDispensedRow("Metformin", "500 mg", "00093512401", 900m),
        };
        var replacement = new[]
        {
            new TopDispensedRow("Atorvastatin", "40 mg", "60505258008", 1m),
        };
        var path = Path.Combine(_tempDir, "command-id.xlsx");
        var writer = new ExcelTop500Writer(
            NullLogger<ExcelTop500Writer>.Instance);

        Assert.True(writer.WriteAtomically(path, first));
        Assert.False(writer.WriteAtomically(path, replacement));
        Assert.Empty(Directory.GetFiles(_tempDir, ".*.tmp.xlsx"));

        var result = new ExcelPricingReader(
            NullLogger<ExcelPricingReader>.Instance).Read(path, "NDC");
        Assert.True(result.Success, result.Error);
        Assert.Single(result.Rows);
        Assert.Equal("00093512401", result.Rows[0].NdcNormalized);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }
}
