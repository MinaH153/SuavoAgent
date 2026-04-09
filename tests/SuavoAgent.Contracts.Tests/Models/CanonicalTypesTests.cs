using SuavoAgent.Contracts.Models;
using Xunit;

namespace SuavoAgent.Contracts.Tests.Models;

public class CanonicalTypesTests
{
    [Fact]
    public void RxReadyForDelivery_CreatesWithAllFields()
    {
        var rx = new RxReadyForDelivery(
            RxNumber: "12345", FillNumber: 2, DrugName: "Lisinopril 10mg",
            Ndc: "00093-7180-01", Quantity: 30m, DaysSupply: 30,
            StatusText: "Ready", IsControlled: false, DrugSchedule: null,
            PatientIdRequired: false, CounselingRequired: true,
            DetectedAt: DateTimeOffset.UtcNow, Source: DetectionSource.Sql);
        Assert.Equal("12345", rx.RxNumber);
        Assert.Equal(DetectionSource.Sql, rx.Source);
    }

    [Fact]
    public void WritebackReceipt_SuccessAndFailure()
    {
        var success = new WritebackReceipt(true, "txn-1", null, WritebackMethod.Api, true, DateTimeOffset.UtcNow);
        Assert.True(success.Success);

        var fail = new WritebackReceipt(false, null, "not found", WritebackMethod.Uia, false, DateTimeOffset.UtcNow);
        Assert.False(fail.Success);
        Assert.NotNull(fail.Error);
    }

    [Fact]
    public void CapabilityManifest_ReportsEngines()
    {
        var m = new CapabilityManifest(true, false, false, true, false, "2024.1", "192.168.0.10:1433", null, new[] { "Point of Sale" });
        Assert.True(m.CanReadSql);
        Assert.False(m.CanReadApi);
        Assert.Single(m.DiscoveredScreens);
    }
}
