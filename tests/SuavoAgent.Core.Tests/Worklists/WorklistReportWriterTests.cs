using ClosedXML.Excel;
using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Core.Worklists;
using Xunit;

namespace SuavoAgent.Core.Tests.Worklists;

public sealed class WorklistReportWriterTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "suavo-worklist-writer-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Write_EmitsTypedAuditableWorkbookWithoutTouchingPms()
    {
        var writer = new WorklistReportWriter(NullLogger<WorklistReportWriter>.Instance);
        IReadOnlyList<IReadOnlyList<object?>> rows = new[]
        {
            (IReadOnlyList<object?>)new object?[]
            {
                "NDC-HASH", 7, 12.34567m, new DateOnly(2026, 7, 12), null,
            },
        };

        var result = writer.Write(
            _directory,
            "inventory",
            "Reorder",
            new[] { "Item", "Quantity", "Unit Cost", "Date", "Optional" },
            rows,
            "20260712T120000Z");

        Assert.True(result.Success, result.Error);
        Assert.Equal(1, result.Rows);
        Assert.Equal(0, result.Skipped);
        Assert.NotNull(result.OutputPath);
        Assert.True(File.Exists(result.OutputPath));

        using var workbook = new XLWorkbook(result.OutputPath);
        var sheet = workbook.Worksheet("Reorder");
        Assert.True(sheet.Cell(1, 1).Style.Font.Bold);
        Assert.Equal("NDC-HASH", sheet.Cell(2, 1).GetString());
        Assert.Equal(7, sheet.Cell(2, 2).GetValue<int>());
        Assert.Equal(12.34567m, sheet.Cell(2, 3).GetValue<decimal>());
        Assert.Equal("0.0000", sheet.Cell(2, 3).Style.NumberFormat.Format);
        Assert.Equal(new DateTime(2026, 7, 12), sheet.Cell(2, 4).GetDateTime());
        Assert.Equal("yyyy-mm-dd", sheet.Cell(2, 4).Style.NumberFormat.Format);
        Assert.True(sheet.Cell(2, 5).IsEmpty());
    }

    [Fact]
    public void Write_InvalidSheetNameReturnsStructuralFailureWithoutPartialSuccess()
    {
        var writer = new WorklistReportWriter(NullLogger<WorklistReportWriter>.Instance);

        var result = writer.Write(
            _directory,
            "inventory",
            "invalid/name",
            new[] { "Item" },
            new[] { (IReadOnlyList<object?>)new object?[] { "value" } },
            "20260712T120000Z");

        Assert.False(result.Success);
        Assert.Null(result.OutputPath);
        Assert.Equal("Worklist write failed", result.Error);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }
}
