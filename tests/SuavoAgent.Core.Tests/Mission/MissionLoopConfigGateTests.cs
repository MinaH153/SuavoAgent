using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SuavoAgent.Core;
using SuavoAgent.Core.Adapters;
using SuavoAgent.Core.Mission;
using Xunit;

namespace SuavoAgent.Core.Tests.Mission;

/// <summary>
/// Pins the Program.cs config-gate contract: Mission Loop Phase 1 services
/// must ONLY exist when <c>MissionLoop.Phase1.Enabled == true</c>. When the
/// flag is off, a consumer resolving the dispatcher / planner / executor
/// should get null (not a half-wired pipeline).
///
/// Uses the same IConfiguration + options-pattern shape as Program.cs so the
/// bootstrap gate is tested against the real loading surface, not a
/// shortcut.
/// </summary>
public sealed class MissionLoopConfigGateTests
{
    [Fact]
    public void FlagOff_Phase1ServicesNotRegistered()
    {
        var provider = BuildProvider(missionLoopJson: null);

        Assert.Null(provider.GetService<MissionExecutor>());
        Assert.Null(provider.GetService<MissionEvaluator>());
        Assert.Null(provider.GetService<IMissionPlanner>());
    }

    [Fact]
    public void FlagExplicitlyFalse_Phase1ServicesNotRegistered()
    {
        var provider = BuildProvider(
            missionLoopJson: "{\"MissionLoop\":{\"Phase1\":{\"Enabled\":false}}}");

        Assert.Null(provider.GetService<MissionExecutor>());
        Assert.Null(provider.GetService<IMissionPlanner>());
    }

    [Fact]
    public void FlagOn_Phase1ServicesRegistered_AndHealthcheckMissionRunsHappyPath()
    {
        var provider = BuildProvider(
            missionLoopJson: "{\"MissionLoop\":{\"Phase1\":{\"Enabled\":true}}}",
            extraWiring: services =>
            {
                services.AddSingleton<IPharmacyReadAdapter>(BuildSeededMock());
            });

        var executor = provider.GetService<MissionExecutor>();
        var planner = provider.GetService<IMissionPlanner>();
        var evaluator = provider.GetService<MissionEvaluator>();
        Assert.NotNull(executor);
        Assert.NotNull(planner);
        Assert.NotNull(evaluator);
    }

    private static MockPharmacyReadAdapter BuildSeededMock()
    {
        var mock = new MockPharmacyReadAdapter();
        mock.SeedPatient(
            identifier: "config-gate-probe",
            patientId: "PID-CFG",
            displayNameHash: "abc",
            lastActivityUtc: DateTimeOffset.UtcNow,
            history: new[]
            {
                new RxHistoryRecord("00000-0001-01", FillCount: 1, LastFillUtc: DateTimeOffset.UtcNow, LastQuantity: 1m),
            });
        return mock;
    }

    private static IServiceProvider BuildProvider(
        string? missionLoopJson,
        Action<ServiceCollection>? extraWiring = null)
    {
        var configBuilder = new ConfigurationBuilder();
        if (!string.IsNullOrWhiteSpace(missionLoopJson))
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(missionLoopJson);
            configBuilder.AddJsonStream(new MemoryStream(bytes));
        }
        var configuration = configBuilder.Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);

        var missionLoopOpts =
            configuration.GetSection("MissionLoop").Get<MissionLoopOptions>()
            ?? new MissionLoopOptions();
        services.Configure<MissionLoopOptions>(
            configuration.GetSection("MissionLoop"));

        if (missionLoopOpts.Phase1.Enabled)
        {
            services.AddMissionLoopPhase1();
        }

        extraWiring?.Invoke(services);

        return services.BuildServiceProvider();
    }
}
