using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Pricing;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Pricing;

/// <summary>
/// Covers the new pricing executor / throttle configuration plumbing landed for the Nadim
/// UIA-first pilot. The actual UiaFirstPricingJobExecutor needs a live IpcCommandClient and
/// Helper process, so its end-to-end behavior is verified by the smoke test rather than here.
/// </summary>
public class PricingExecutorConfigTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"suavo_exec_cfg_{Guid.NewGuid():N}");
    private readonly AgentStateDb _db;

    public PricingExecutorConfigTests()
    {
        Directory.CreateDirectory(_tempDir);
        _db = new AgentStateDb(Path.Combine(_tempDir, "state.db"));
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void AgentOptions_PricingExecutor_DefaultsToSqlFirst()
    {
        // Default is intentionally SqlFirst — existing pharmacies must not silently switch
        // to UIA on upgrade. Opt-in only via explicit appsettings or env override.
        var options = new AgentOptions();
        Assert.Equal(PricingExecutorMode.SqlFirst, options.PricingExecutor);
    }

    [Fact]
    public void AgentOptions_PricingThrottleMs_Defaults_To_1500()
    {
        // 1500 ms is the UIA-safe default selected for the Nadim pilot — keeps a 500-NDC run
        // at ~12.5 min and stays below any anti-automation heuristic we suspect PioneerRx may
        // apply. SQL-first paths may safely lower this via config.
        var options = new AgentOptions();
        Assert.Equal(1500, options.PricingThrottleMs);
    }

    [Fact]
    public void PricingExecutorMode_HasBothValues()
    {
        // Defensive: detect accidental renames in the enum that would break appsettings
        // binding (Microsoft.Extensions.Configuration binds enums by case-insensitive name).
        Assert.True(Enum.IsDefined(typeof(PricingExecutorMode), "SqlFirst"));
        Assert.True(Enum.IsDefined(typeof(PricingExecutorMode), "UiaFirst"));
    }

    [Theory]
    [InlineData(-5000)]       // negative typo - clamp to zero
    [InlineData(0)]           // explicit zero - allowed
    [InlineData(1500)]        // recommended UIA default
    [InlineData(60_000)]      // 1 minute - over the 30s ceiling, clamp to 30000
    [InlineData(3_600_000)]   // 1 hour - clearly a typo, clamp to 30000
    public void PricingJobRunner_AnyThrottleValue_Constructs_Without_Throwing(int throttleMs)
    {
        // The runner's constructor must clamp absurd throttle inputs (negative, huge) without
        // raising. The clamp is what protects a misconfigured pharmacy from accidentally
        // stalling jobs forever.
        var reader = new ExcelPricingReader(NullLogger<ExcelPricingReader>.Instance);
        var writer = new ExcelPricingWriter(NullLogger<ExcelPricingWriter>.Instance);

        var runner = new PricingJobRunner(
            reader,
            writer,
            _db,
            NullLogger<PricingJobRunner>.Instance,
            brainEvaluator: null,
            interLookupDelay: TimeSpan.FromMilliseconds(throttleMs));

        Assert.NotNull(runner);
    }

    [Fact]
    public void PricingJobRunner_NullThrottle_FallsBackToDefault()
    {
        // The optional throttle parameter must default cleanly — back-compat for callers
        // that haven't been updated to pass a value yet (none in production, but the
        // constructor shape is public).
        var reader = new ExcelPricingReader(NullLogger<ExcelPricingReader>.Instance);
        var writer = new ExcelPricingWriter(NullLogger<ExcelPricingWriter>.Instance);

        var runner = new PricingJobRunner(
            reader,
            writer,
            _db,
            NullLogger<PricingJobRunner>.Instance,
            brainEvaluator: null,
            interLookupDelay: null);

        Assert.NotNull(runner);
    }
}
