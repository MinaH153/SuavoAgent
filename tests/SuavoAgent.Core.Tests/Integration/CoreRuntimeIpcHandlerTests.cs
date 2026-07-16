using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SuavoAgent.Contracts.Behavioral;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Core.Autonomy;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Ipc;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Integration;

/// <summary>
/// Exercises the local authenticated-IPC command surface as composed by the production registration
/// root. The handler is invoked in memory so no named-pipe or Windows identity boundary is bypassed.
/// </summary>
public sealed class CoreRuntimeIpcHandlerTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "suavo-core-ipc-registration-" + Guid.NewGuid().ToString("N"));
    private readonly IHost _host;
    private readonly AgentStateDb _db;
    private readonly Func<IpcRequest, Task<IpcResponse>> _handler;

    public CoreRuntimeIpcHandlerTests()
    {
        Directory.CreateDirectory(_directory);
        _db = new AgentStateDb(Path.Combine(_directory, "state.db"));
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = Array.Empty<string>(),
            DisableDefaults = true,
        });
        builder.Services.AddLogging();
        builder.Services.AddOptions<AgentOptions>().Configure(options =>
        {
            options.AgentId = "ipc-handler-agent";
            options.MachineFingerprint = "ipc-handler-machine";
            options.PharmacyId = "ipc-handler-pharmacy";
            options.Reasoning.Enabled = false;
            options.Reasoning.CloudEnabled = false;
        });
        builder.Services.AddSingleton(_db);
        builder.Services.AddSingleton(new AutopilotRunCoordinator());
        CoreRuntimeServiceRegistration.Register(
            builder,
            "core-ipc-command-test",
            "core-ipc-event-test",
            "core-ipc-nonce-test");
        _host = builder.Build();

        var server = _host.Services.GetRequiredService<IpcPipeServer>();
        var field = typeof(IpcPipeServer).GetField(
            "_handler",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        _handler = Assert.IsType<Func<IpcRequest, Task<IpcResponse>>>(field!.GetValue(server));
    }

    [Fact]
    public async Task GetHealth_ProjectsStructuralRuntimeState()
    {
        var response = await SendAsync(IpcCommands.GetHealth);

        Assert.Equal(IpcStatus.Ok, response.Status);
        Assert.Null(response.Error);
        Assert.NotNull(response.Data);
        var data = response.Data!.Value;
        Assert.Equal("ipc-handler-agent", data.GetProperty("agentId").GetString());
        Assert.Equal("ipc-handler-machine", data.GetProperty("machineFingerprint").GetString());
        Assert.True(data.TryGetProperty("audit", out _));
        Assert.True(data.TryGetProperty("observationReadinessCode", out _) ||
            data.GetProperty("helper").TryGetProperty("observationReadinessCode", out _));
    }

    [Fact]
    public async Task PharmacySalt_NoLearningSession_ReturnsOpaqueDailyDerivedKey()
    {
        var response = await SendAsync(IpcCommands.GetPharmacySalt);

        Assert.Equal(IpcStatus.Ok, response.Status);
        var key = response.Data!.Value.GetString();
        Assert.NotNull(key);
        Assert.NotEmpty(Convert.FromBase64String(key!));
        Assert.DoesNotContain("ipc-handler-pharmacy", key);
    }

    [Fact]
    public async Task PharmacySalt_ActiveLearningSession_DerivesKeyWithoutExposingMasterSalt()
    {
        const string session = "ipc-handler-session";
        _db.CreateLearningSession(session, "ipc-handler-pharmacy");
        var masterSalt = _db.GetOrCreateHmacSalt(session);

        var response = await SendAsync(IpcCommands.GetPharmacySalt);

        var key = response.Data!.Value.GetString();
        Assert.NotNull(key);
        Assert.NotEqual(masterSalt, key);
        Assert.Equal(32, Convert.FromBase64String(key!).Length);
    }

    [Fact]
    public async Task ObservationLease_NullRequestIssuesLeaseAndCurrentIdReusesIt()
    {
        var first = await SendAsync(IpcCommands.GetObservationLease);
        var firstLease = first.Data!.Value.Deserialize<ObservationKeyLease>();
        Assert.NotNull(firstLease);
        Assert.Equal(ObservationKeyLease.CurrentContractVersion, firstLease.ContractVersion);
        Assert.Equal(32, Convert.FromBase64String(firstLease.KeyMaterial).Length);

        var second = await SendAsync(
            IpcCommands.GetObservationLease,
            JsonSerializer.SerializeToElement(new ObservationKeyLeaseRequest
            {
                CurrentLeaseId = firstLease.LeaseId,
            }));
        var secondLease = second.Data!.Value.Deserialize<ObservationKeyLease>();

        Assert.NotNull(secondLease);
        Assert.Equal(firstLease.LeaseId, secondLease.LeaseId);
        Assert.Equal(firstLease.Epoch, secondLease.Epoch);
    }

    [Fact]
    public async Task AutopilotControlState_ReportsInitialClosedWorldState()
    {
        var response = await SendAsync(IpcCommands.GetAutopilotControlState);
        var state = response.Data!.Value.Deserialize<AutopilotRuntimeState>();

        Assert.Equal(IpcStatus.Ok, response.Status);
        Assert.NotNull(state);
        Assert.Equal(0, state.ControlGeneration);
        Assert.False(state.Paused);
        Assert.False(state.Stopped);
        Assert.Empty(state.ActiveKinds);
    }

    [Fact]
    public async Task AutopilotControl_ValidPauseResumeStopSequenceIsGenerationBound()
    {
        var pause = await ControlAsync("pause", null);
        Assert.Equal(IpcStatus.Ok, pause.Status);
        var paused = pause.Data!.Value.Deserialize<AutopilotControlReceipt>();
        Assert.NotNull(paused);
        Assert.True(paused.Applied);
        Assert.Equal("paused", paused.Code);

        var resume = await ControlAsync("resume", paused.ControlGeneration);
        var resumed = resume.Data!.Value.Deserialize<AutopilotControlReceipt>();
        Assert.NotNull(resumed);
        Assert.True(resumed.Applied);
        Assert.Equal("resumed", resumed.Code);

        var stop = await ControlAsync("stop", null);
        var stopped = stop.Data!.Value.Deserialize<AutopilotControlReceipt>();
        Assert.NotNull(stopped);
        Assert.True(stopped.Applied);
        Assert.True(stopped.Stopped);

        var reopen = await ControlAsync("resume", stopped.ControlGeneration);
        var stopLatched = reopen.Data!.Value.Deserialize<AutopilotControlReceipt>();
        Assert.NotNull(stopLatched);
        Assert.False(stopLatched.Applied);
        Assert.Equal("stop_latched", stopLatched.Code);
    }

    public static TheoryData<JsonElement?> InvalidControls => new()
    {
        null,
        JsonSerializer.SerializeToElement("not-an-object"),
        JsonSerializer.SerializeToElement(new AutopilotControlRequest(0, "pause", "companion_control", null)),
        JsonSerializer.SerializeToElement(new AutopilotControlRequest(1, "unknown", "companion_control", null)),
        JsonSerializer.SerializeToElement(new AutopilotControlRequest(1, "pause", "wrong_reason", null)),
        JsonSerializer.SerializeToElement(new AutopilotControlRequest(1, "pause", "companion_control", 0)),
        JsonSerializer.SerializeToElement(new AutopilotControlRequest(1, "stop", "companion_control", 0)),
        JsonSerializer.SerializeToElement(new AutopilotControlRequest(1, "resume", "companion_control", null)),
    };

    [Theory]
    [MemberData(nameof(InvalidControls))]
    public async Task AutopilotControl_MalformedOrUnboundRequestIsRejected(JsonElement? data)
    {
        var response = await SendAsync(IpcCommands.AutopilotControl, data);

        Assert.Equal(IpcStatus.BadRequest, response.Status);
        Assert.Equal("autopilot_control_invalid", response.Error?.Code);
        Assert.Null(response.Data);
    }

    [Fact]
    public async Task AutopilotControl_MalformedObjectIsRejectedWithoutThrowing()
    {
        var response = await SendAsync(
            IpcCommands.AutopilotControl,
            JsonSerializer.Deserialize<JsonElement>("{\"ContractVersion\":\"wrong\"}"));

        Assert.Equal(IpcStatus.BadRequest, response.Status);
        Assert.Equal("autopilot_control_invalid", response.Error?.Code);
    }

    [Theory]
    [InlineData(IpcCommands.HelperStatus)]
    [InlineData(IpcCommands.HelperError)]
    public async Task HelperStatus_OnlyAllowlistedStructuralCodeIsPersisted(string command)
    {
        var accepted = await SendAsync(
            command,
            JsonSerializer.SerializeToElement(new { code = "observation_spool_healthy" }));

        Assert.Equal(IpcStatus.Ok, accepted.Status);
        Assert.Equal(
            "observation_spool_healthy",
            _db.GetBehavioralDeliveryHealth(null).ObservationSpoolStatus);

        await SendAsync(
            command,
            JsonSerializer.SerializeToElement(new { code = "patient name must not persist" }));
        Assert.Equal(
            "observation_spool_healthy",
            _db.GetBehavioralDeliveryHealth(null).ObservationSpoolStatus);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown")]
    [InlineData("observation_bad.code")]
    public async Task HelperStatus_InvalidShapeOrCodeIsIgnored(string? code)
    {
        JsonElement? data = code is null
            ? null
            : JsonSerializer.SerializeToElement(new { code });

        var response = await SendAsync(IpcCommands.HelperStatus, data);

        Assert.Equal(IpcStatus.Ok, response.Status);
        Assert.Equal("not_reported", _db.GetBehavioralDeliveryHealth(null).ObservationSpoolStatus);
    }

    [Fact]
    public async Task UnknownLocalCommandReceivesExplicitNoDataSuccess()
    {
        var response = await SendAsync("unknown_local_command");

        Assert.Equal(IpcStatus.Ok, response.Status);
        Assert.Equal("unknown_local_command", response.Command);
        Assert.Null(response.Data);
        Assert.Null(response.Error);
    }

    private Task<IpcResponse> ControlAsync(string action, long? expectedGeneration) =>
        SendAsync(
            IpcCommands.AutopilotControl,
            JsonSerializer.SerializeToElement(new AutopilotControlRequest(
                AutopilotControlRequest.CurrentContractVersion,
                action,
                "companion_control",
                expectedGeneration)));

    private Task<IpcResponse> SendAsync(string command, JsonElement? data = null) =>
        _handler(new IpcRequest(Guid.NewGuid().ToString("N"), command, 1, data));

    public void Dispose()
    {
        _host.Dispose();
        _db.Dispose();
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }
}
