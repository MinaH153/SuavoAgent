using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Verbs;
using SuavoAgent.Verbs.Verbs;
using Xunit;

namespace SuavoAgent.Verbs.Tests;

public class VerbDispatcherTests
{
    private sealed class FakeServiceController : IServiceController
    {
        public Dictionary<string, ServiceState> States { get; } = new();
        public List<string> StartCalls { get; } = new();
        public bool StartShouldSucceed { get; set; } = true;

        public ServiceState Query(string serviceName) =>
            States.TryGetValue(serviceName, out var s) ? s : ServiceState.Unknown;

        public bool Start(string serviceName, TimeSpan timeout)
        {
            StartCalls.Add(serviceName);
            if (StartShouldSucceed)
            {
                States[serviceName] = ServiceState.Running;
                return true;
            }
            return false;
        }

        public bool Stop(string serviceName, TimeSpan timeout)
        {
            States[serviceName] = ServiceState.Stopped;
            return true;
        }
    }

    private sealed class FakeSignatureVerifier : ISignatureVerifier
    {
        public bool Result { get; set; } = true;
        public bool Verify(SignedVerbInvocation invocation) => Result;
    }

    private sealed class FakeFence : IFenceProvider
    {
        public Guid CurrentFenceId { get; set; } = Guid.Parse("11111111-1111-1111-1111-111111111111");
    }

    private static (VerbDispatcher Dispatcher, FakeServiceController Svc, FakeSignatureVerifier Sig, FakeFence Fence, VerbRegistry Reg) Setup()
    {
        var svc = new FakeServiceController
        {
            States = { ["SuavoAgent.Core"] = ServiceState.Stopped }
        };
        var sig = new FakeSignatureVerifier();
        var fence = new FakeFence();
        var reg = new VerbRegistry(new IVerb[] { new RestartServiceVerb() });
        var services = new ServiceCollection()
            .AddSingleton<IServiceController>(svc)
            .BuildServiceProvider();
        var dispatcher = new VerbDispatcher(reg, sig, fence, services, NullLogger<VerbDispatcher>.Instance);
        return (dispatcher, svc, sig, fence, reg);
    }

    private static SignedVerbInvocation MakeInvocation(
        string verb, string version, string schemaHash, Guid fenceId,
        Dictionary<string, object?> parameters)
    {
        return new SignedVerbInvocation(
            InvocationId: Guid.NewGuid(),
            VerbName: verb,
            VerbVersion: version,
            SchemaHash: schemaHash,
            Parameters: parameters,
            FenceId: fenceId,
            PharmacyId: "ph",
            SignedAt: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Signature: "stub");
    }

    [Fact]
    public async Task Dispatch_HappyPath_Succeeds()
    {
        var (dispatcher, svc, _, fence, reg) = Setup();
        var schemaHash = reg.SchemaHash("restart_service", "1.0.0");
        var inv = MakeInvocation("restart_service", "1.0.0", schemaHash, fence.CurrentFenceId,
            new() { ["service_name"] = "SuavoAgent.Core" });

        var result = await dispatcher.DispatchAsync(inv, CancellationToken.None);

        Assert.Equal(VerbDispatchStatus.Success, result.Status);
        Assert.Single(svc.StartCalls);
        Assert.Equal("SuavoAgent.Core", svc.StartCalls[0]);
    }

    [Fact]
    public async Task Dispatch_SchemaVersionMismatch_Rejects()
    {
        var (dispatcher, _, _, fence, _) = Setup();
        var inv = MakeInvocation("restart_service", "1.0.0", "bad_hash", fence.CurrentFenceId,
            new() { ["service_name"] = "SuavoAgent.Core" });

        var result = await dispatcher.DispatchAsync(inv, CancellationToken.None);

        Assert.Equal(VerbDispatchStatus.SchemaVersionMismatch, result.Status);
    }

    [Fact]
    public async Task Dispatch_InvalidSignature_Rejects()
    {
        var (dispatcher, _, sig, fence, reg) = Setup();
        sig.Result = false;
        var schemaHash = reg.SchemaHash("restart_service", "1.0.0");
        var inv = MakeInvocation("restart_service", "1.0.0", schemaHash, fence.CurrentFenceId,
            new() { ["service_name"] = "SuavoAgent.Core" });

        var result = await dispatcher.DispatchAsync(inv, CancellationToken.None);

        Assert.Equal(VerbDispatchStatus.SignatureInvalid, result.Status);
    }

    [Fact]
    public async Task Dispatch_FenceMismatch_Rejects()
    {
        var (dispatcher, _, _, fence, reg) = Setup();
        var schemaHash = reg.SchemaHash("restart_service", "1.0.0");
        var inv = MakeInvocation("restart_service", "1.0.0", schemaHash, Guid.NewGuid(),
            new() { ["service_name"] = "SuavoAgent.Core" });

        var result = await dispatcher.DispatchAsync(inv, CancellationToken.None);

        Assert.Equal(VerbDispatchStatus.FenceMismatch, result.Status);
    }

    [Fact]
    public async Task Dispatch_TimestampSkew_Rejects()
    {
        var (dispatcher, _, _, fence, reg) = Setup();
        var schemaHash = reg.SchemaHash("restart_service", "1.0.0");
        var inv = new SignedVerbInvocation(
            InvocationId: Guid.NewGuid(),
            VerbName: "restart_service",
            VerbVersion: "1.0.0",
            SchemaHash: schemaHash,
            Parameters: new Dictionary<string, object?> { ["service_name"] = "SuavoAgent.Core" },
            FenceId: fence.CurrentFenceId,
            PharmacyId: "ph",
            SignedAt: DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 3600, // 1h ago
            Signature: "stub");

        var result = await dispatcher.DispatchAsync(inv, CancellationToken.None);

        Assert.Equal(VerbDispatchStatus.TimestampSkew, result.Status);
    }

    [Fact]
    public async Task Dispatch_MissingRequiredParameter_Rejects()
    {
        var (dispatcher, _, _, fence, reg) = Setup();
        var schemaHash = reg.SchemaHash("restart_service", "1.0.0");
        var inv = MakeInvocation("restart_service", "1.0.0", schemaHash, fence.CurrentFenceId,
            new Dictionary<string, object?>());

        var result = await dispatcher.DispatchAsync(inv, CancellationToken.None);

        Assert.Equal(VerbDispatchStatus.ParameterValidationFailed, result.Status);
    }

    [Fact]
    public async Task Dispatch_EnumHintViolation_FailsAtParameterValidation()
    {
        var (dispatcher, _, _, fence, reg) = Setup();
        var schemaHash = reg.SchemaHash("restart_service", "1.0.0");
        var inv = MakeInvocation("restart_service", "1.0.0", schemaHash, fence.CurrentFenceId,
            new() { ["service_name"] = "NotARealService" });

        var result = await dispatcher.DispatchAsync(inv, CancellationToken.None);

        // Enum-hint validation fires before preconditions per the ValidateParameters
        // step in VerbDispatcher.DispatchAsync — param validation is structural,
        // preconditions are semantic.
        Assert.Equal(VerbDispatchStatus.ParameterValidationFailed, result.Status);
    }

    [Fact]
    public async Task Dispatch_ServiceNotInstalled_FailsAtPrecondition()
    {
        var (dispatcher, svc, _, fence, reg) = Setup();
        // Valid enum value but service not installed on this machine
        svc.States.Clear();
        svc.States["SuavoAgent.Broker"] = ServiceState.NotInstalled;
        var schemaHash = reg.SchemaHash("restart_service", "1.0.0");
        var inv = MakeInvocation("restart_service", "1.0.0", schemaHash, fence.CurrentFenceId,
            new() { ["service_name"] = "SuavoAgent.Broker" });

        var result = await dispatcher.DispatchAsync(inv, CancellationToken.None);

        Assert.Equal(VerbDispatchStatus.PreconditionFailed, result.Status);
    }

    [Fact]
    public async Task Dispatch_UnknownVerb_Rejects()
    {
        var (dispatcher, _, _, fence, _) = Setup();
        var inv = MakeInvocation("never_was", "1.0.0", "whatever", fence.CurrentFenceId,
            new Dictionary<string, object?>());

        var result = await dispatcher.DispatchAsync(inv, CancellationToken.None);

        Assert.Equal(VerbDispatchStatus.UnknownVerb, result.Status);
    }

    [Fact]
    public async Task Dispatch_ExecuteFailure_InvokesRollback()
    {
        var (dispatcher, svc, _, fence, reg) = Setup();
        svc.StartShouldSucceed = false; // sc.exe start fails
        var schemaHash = reg.SchemaHash("restart_service", "1.0.0");
        var inv = MakeInvocation("restart_service", "1.0.0", schemaHash, fence.CurrentFenceId,
            new() { ["service_name"] = "SuavoAgent.Core" });

        var result = await dispatcher.DispatchAsync(inv, CancellationToken.None);

        Assert.Equal(VerbDispatchStatus.RolledBack, result.Status);
        Assert.NotNull(result.RollbackEnvelope);
    }

    [Fact]
    public void ParameterValidation_NullValue_Rejected()
    {
        var schema = new VerbParameterSchema(new[]
        {
            new VerbParameterDefinition("x", typeof(string))
        });
        var err = VerbDispatcher.ValidateParameters(schema, new Dictionary<string, object?> { ["x"] = null });
        Assert.Contains("null", err);
    }
}
