using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SuavoAgent.Core.ActionGrammarV1.Workflows;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Ipc;
using SuavoAgent.Core.Learning;
using SuavoAgent.Core.Pricing;
using SuavoAgent.Core.Reasoning;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Integration;

public sealed class CoreRuntimeServiceRegistrationTests
{
    [Fact]
    public void DisabledReasoningRuntime_ResolvesFailClosedConcreteGraph()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "suavo-core-registration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var dbPath = Path.Combine(directory, "state.db");

        try
        {
            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
            {
                Args = Array.Empty<string>(),
                DisableDefaults = true,
            });
            builder.Services.AddLogging();
            builder.Services.AddOptions<AgentOptions>().Configure(options =>
            {
                options.AgentId = "agent-registration";
                options.MachineFingerprint = "machine-registration";
                options.PharmacyId = "pharmacy-registration";
                options.PricingExecutor = PricingExecutorMode.SqlFirst;
                options.Reasoning.Enabled = false;
                options.Reasoning.CloudEnabled = false;
            });
            builder.Services.AddSingleton(new AgentStateDb(dbPath));

            CoreRuntimeServiceRegistration.Register(
                builder,
                "suavo-core-registration-command",
                "suavo-core-registration-events",
                "nonce-registration");

            Assert.Contains(builder.Services, descriptor => descriptor.ServiceType == typeof(IIpcCommandClient));
            Assert.Contains(builder.Services, descriptor => descriptor.ServiceType == typeof(IPricingJobExecutor));
            Assert.Contains(builder.Services, descriptor => descriptor.ServiceType == typeof(IWorkflowAuditClient));
            Assert.Contains(builder.Services, descriptor => descriptor.ServiceType == typeof(IActiveLearnedRuleRegistry));
            Assert.Contains(builder.Services, descriptor => descriptor.ServiceType == typeof(ILocalInference));
            Assert.Contains(builder.Services, descriptor => descriptor.ServiceType == typeof(ICloudReasoning));
            Assert.Contains(builder.Services, descriptor => descriptor.ServiceType == typeof(IpcPipeServer));

            using var host = builder.Build();
            var services = host.Services;

            Assert.IsType<SqlFirstPricingJobExecutor>(services.GetRequiredService<IPricingJobExecutor>());
            Assert.IsType<NullWorkflowAuditClient>(services.GetRequiredService<IWorkflowAuditClient>());
            Assert.IsType<NullLocalInference>(services.GetRequiredService<ILocalInference>());
            Assert.IsType<NullCloudReasoning>(services.GetRequiredService<ICloudReasoning>());
            Assert.IsType<ActiveLearnedRuleRegistry>(services.GetRequiredService<IActiveLearnedRuleRegistry>());
            Assert.NotNull(services.GetRequiredService<RuleEngine>());
            Assert.NotNull(services.GetRequiredService<TieredBrain>());
            Assert.NotNull(services.GetRequiredService<WorkflowExecutor>());
            Assert.NotNull(services.GetRequiredService<IpcPipeServer>());
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }
}
