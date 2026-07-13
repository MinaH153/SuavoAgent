using Serilog;
using SuavoAgent.Contracts.Behavioral;
using SuavoAgent.Helper.SystemObservers;
using Xunit;

namespace SuavoAgent.Helper.Tests.SystemObservers;

public sealed class SpreadsheetStructureObserverTests
{
    [Fact]
    public async Task FocusedWorkbook_EmitsOnlyFileTypeAndHmacTitle()
    {
        var events = new List<BehavioralEvent>();
        using var buffer = new BehavioralEventBuffer(
            10,
            10,
            batch =>
            {
                events.AddRange(batch);
                return Task.CompletedTask;
            });
        using var logger = new LoggerConfiguration().CreateLogger();
        var observer = new SpreadsheetStructureObserver(buffer, "daily-salt", logger);

        observer.OnSpreadsheetFocused("Patient Delivery List.xlsx - Excel");
        await buffer.FlushAsync();

        var behavioralEvent = Assert.Single(events);
        Assert.Equal("spreadsheet_open", behavioralEvent.Subtype);
        Assert.Equal("xlsx", behavioralEvent.ElementId);
        Assert.DoesNotContain("Patient", behavioralEvent.NameHash!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Delivery", behavioralEvent.NameHash!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("EXCEL")]
    [InlineData("excel")]
    [InlineData("soffice")]
    [InlineData("wps")]
    public void SpreadsheetProcessDetection_IsCaseInsensitive(string processName)
    {
        Assert.True(SpreadsheetStructureObserver.IsSpreadsheetProcess(processName));
    }
}
