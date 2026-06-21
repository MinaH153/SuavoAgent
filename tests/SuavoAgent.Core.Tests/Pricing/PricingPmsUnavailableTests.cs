using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Core.Pricing;
using Xunit;

namespace SuavoAgent.Core.Tests.Pricing;

/// <summary>QA I2: the signal that halts a pricing job when PioneerRx isn't attached, instead of
/// grinding the workbook into all-error rows and reporting a green "Completed" that priced nothing.</summary>
public class PricingPmsUnavailableTests
{
    private static SupplierPriceResult R(bool found, string? err) =>
        new("job", 1, "12345678901", found, null, null, err);

    [Fact]
    public void Pms_not_attached_error_is_detected()
        => Assert.True(PricingJobRunner.IsPmsUnavailable(R(false, "PioneerRx main window not available")));

    [Fact]
    public void A_normal_not_found_is_not_pms_unavailable()
        => Assert.False(PricingJobRunner.IsPmsUnavailable(R(false, "NO_MATCH")));

    [Fact]
    public void A_found_result_is_not_pms_unavailable()
        => Assert.False(PricingJobRunner.IsPmsUnavailable(R(true, null)));

    [Fact]
    public void Null_error_is_not_pms_unavailable()
        => Assert.False(PricingJobRunner.IsPmsUnavailable(R(false, null)));
}
