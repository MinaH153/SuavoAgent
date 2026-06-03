using System;
using SuavoAgent.Contracts.Models;
using SuavoAgent.Core.Health;
using Xunit;

namespace SuavoAgent.Core.Tests.Health;

public class HealthCompositeCalculatorTests
{
    private const int ExtractionWindowMinutes = 30;
    private static readonly DateTimeOffset Now =
        new(2026, 5, 2, 14, 0, 0, TimeSpan.Zero); // Saturday 2pm UTC

    [Fact]
    public void AllSignalsTrue_BusinessHours_ReturnsHealthy()
    {
        var calc = NewCalculator(insideBusinessHours: true);
        var snapshot = new HealthSignalsSnapshot(
            HelperAttached: true,
            IpcConnected: true,
            SchemaCanaryGreen: true,
            LastExtractionAt: Now.AddMinutes(-5));

        var result = calc.Compute(snapshot, Now);

        Assert.Equal("healthy", result.Status);
        Assert.True(result.Components.HelperAttached);
        Assert.True(result.Components.IpcConnected);
        Assert.True(result.Components.SchemaCanaryGreen);
        Assert.True(result.Components.ExtractionRecent);
    }

    [Fact]
    public void AllSignalsTrue_OutsideBusinessHours_ReturnsHealthy()
    {
        var calc = NewCalculator(insideBusinessHours: false);
        var snapshot = new HealthSignalsSnapshot(
            HelperAttached: true,
            IpcConnected: true,
            SchemaCanaryGreen: true,
            LastExtractionAt: Now.AddHours(-12));

        var result = calc.Compute(snapshot, Now);

        Assert.Equal("healthy", result.Status);
        Assert.True(result.Components.ExtractionRecent);
    }

    [Fact]
    public void HelperDisconnected_ReturnsDegraded()
    {
        var calc = NewCalculator(insideBusinessHours: true);
        var snapshot = new HealthSignalsSnapshot(
            HelperAttached: false,
            IpcConnected: true,
            SchemaCanaryGreen: true,
            LastExtractionAt: Now.AddMinutes(-5));

        var result = calc.Compute(snapshot, Now);

        Assert.Equal("heartbeating-but-unhealthy", result.Status);
        Assert.False(result.Components.HelperAttached);
        Assert.True(result.Components.IpcConnected);
    }

    [Fact]
    public void ExtractionStale_BusinessHours_ReturnsDegraded()
    {
        var calc = NewCalculator(insideBusinessHours: true);
        var snapshot = new HealthSignalsSnapshot(
            HelperAttached: true,
            IpcConnected: true,
            SchemaCanaryGreen: true,
            LastExtractionAt: Now.AddMinutes(-31));

        var result = calc.Compute(snapshot, Now);

        Assert.Equal("heartbeating-but-unhealthy", result.Status);
        Assert.False(result.Components.ExtractionRecent);
    }

    [Fact]
    public void ExtractionStale_OutsideBusinessHours_ReturnsHealthy()
    {
        var calc = NewCalculator(insideBusinessHours: false);
        var snapshot = new HealthSignalsSnapshot(
            HelperAttached: true,
            IpcConnected: true,
            SchemaCanaryGreen: true,
            LastExtractionAt: Now.AddHours(-12));

        var result = calc.Compute(snapshot, Now);

        Assert.Equal("healthy", result.Status);
        Assert.True(result.Components.ExtractionRecent);
    }

    [Fact]
    public void LastExtractionNull_BusinessHours_ReturnsDegraded()
    {
        var calc = NewCalculator(insideBusinessHours: true);
        var snapshot = new HealthSignalsSnapshot(
            HelperAttached: true,
            IpcConnected: true,
            SchemaCanaryGreen: true,
            LastExtractionAt: null);

        var result = calc.Compute(snapshot, Now);

        Assert.Equal("heartbeating-but-unhealthy", result.Status);
        Assert.False(result.Components.ExtractionRecent);
    }

    [Fact]
    public void AllSignalsFalse_ReturnsDegraded_AllComponentsFalse()
    {
        var calc = NewCalculator(insideBusinessHours: true);
        var snapshot = new HealthSignalsSnapshot(
            HelperAttached: false,
            IpcConnected: false,
            SchemaCanaryGreen: false,
            LastExtractionAt: null);

        var result = calc.Compute(snapshot, Now);

        Assert.Equal("heartbeating-but-unhealthy", result.Status);
        Assert.False(result.Components.HelperAttached);
        Assert.False(result.Components.IpcConnected);
        Assert.False(result.Components.SchemaCanaryGreen);
        Assert.False(result.Components.ExtractionRecent);
    }

    [Fact]
    public void BusinessHoursLookupThrows_FallsBackToOffHours()
    {
        var calc = new HealthCompositeCalculator(
            new ThrowingBusinessHoursProvider(),
            extractionWindowMinutes: ExtractionWindowMinutes);
        var snapshot = new HealthSignalsSnapshot(
            HelperAttached: true,
            IpcConnected: true,
            SchemaCanaryGreen: true,
            LastExtractionAt: Now.AddHours(-12));

        var result = calc.Compute(snapshot, Now);

        Assert.Equal("healthy", result.Status);
        Assert.True(result.Components.ExtractionRecent);
    }

    [Fact]
    public void ComputedAt_MatchesClockArgument()
    {
        var calc = NewCalculator(insideBusinessHours: true);
        var snapshot = new HealthSignalsSnapshot(true, true, true, Now);

        var result = calc.Compute(snapshot, Now);

        Assert.Equal(Now, result.ComputedAt);
    }

    private static HealthCompositeCalculator NewCalculator(bool insideBusinessHours) =>
        new(new FakeBusinessHoursProvider(insideBusinessHours), ExtractionWindowMinutes);

    private sealed class FakeBusinessHoursProvider : IBusinessHoursProvider
    {
        private readonly bool _inside;
        public FakeBusinessHoursProvider(bool inside) => _inside = inside;
        public bool IsInsideBusinessHours(DateTimeOffset at) => _inside;
    }

    private sealed class ThrowingBusinessHoursProvider : IBusinessHoursProvider
    {
        public bool IsInsideBusinessHours(DateTimeOffset at) =>
            throw new InvalidOperationException("hours table down");
    }
}
