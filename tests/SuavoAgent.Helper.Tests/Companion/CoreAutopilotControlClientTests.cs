using System.Text.Json;
using Serilog;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Helper.Companion;
using Xunit;

namespace SuavoAgent.Helper.Tests.Companion;

public sealed class CoreAutopilotControlClientTests
{
    [Fact]
    public async Task ResumeBindsToTheExactCoreGeneration()
    {
        AutopilotControlRequest? control = null;
        var client = new CoreAutopilotControlClient(
            (command, payload, _) =>
            {
                if (command == IpcCommands.GetAutopilotControlState)
                {
                    return Task.FromResult<IpcResponse?>(Ok(
                        command,
                        new { ControlGeneration = 17L, Paused = true, Stopped = false }));
                }

                control = JsonSerializer.Deserialize<AutopilotControlRequest>(payload!);
                return Task.FromResult<IpcResponse?>(Ok(command, new
                {
                    Action = 1,
                    Applied = true,
                    Paused = false,
                    Stopped = false,
                }));
            },
            new LoggerConfiguration().CreateLogger());

        var applied = await client.ResumeAsync(CancellationToken.None);

        Assert.True(applied);
        Assert.NotNull(control);
        Assert.Equal("resume", control!.Action);
        Assert.Equal(17, control.ExpectedControlGeneration);
        Assert.Equal("companion_control", control.ReasonCode);
    }

    [Theory]
    [InlineData(false, true, 0)]
    [InlineData(true, true, 0)]
    [InlineData(true, false, 1)]
    public async Task ResumeFailsClosedUnlessCoreAcceptsTheGenerationBoundControl(
        bool paused,
        bool stopped,
        int expectedControlCalls)
    {
        var controlCalls = 0;
        var client = new CoreAutopilotControlClient(
            (command, payload, _) =>
            {
                if (command == IpcCommands.AutopilotControl)
                {
                    controlCalls++;
                    var control = JsonSerializer.Deserialize<AutopilotControlRequest>(payload!);
                    Assert.Equal(3, control!.ExpectedControlGeneration);
                    return Task.FromResult<IpcResponse?>(Ok(
                        command,
                        new
                        {
                            Action = 1,
                            Applied = false,
                            Paused = paused,
                            Stopped = stopped,
                        }));
                }
                return Task.FromResult<IpcResponse?>(Ok(
                    command,
                    new { ControlGeneration = 3L, Paused = paused, Stopped = stopped }));
            },
            new LoggerConfiguration().CreateLogger());

        Assert.False(await client.ResumeAsync(CancellationToken.None));
        Assert.Equal(expectedControlCalls, controlCalls);
    }

    [Fact]
    public async Task MissingOrMalformedAcknowledgementNeverReportsApplied()
    {
        var client = new CoreAutopilotControlClient(
            (_, _, _) => Task.FromResult<IpcResponse?>(null),
            new LoggerConfiguration().CreateLogger());

        Assert.False(await client.PauseAsync(CancellationToken.None));
        Assert.False(await client.StopAsync(CancellationToken.None));
    }

    [Fact]
    public async Task LocalHumanPauseCanResumeWhenCoreProvesAlreadyOpen()
    {
        var controls = 0;
        var client = new CoreAutopilotControlClient(
            (command, _, _) =>
            {
                if (command == IpcCommands.AutopilotControl) controls++;
                return Task.FromResult<IpcResponse?>(Ok(
                    command,
                    new
                    {
                        ControlGeneration = 9L,
                        Paused = false,
                        Stopped = false,
                    }));
            },
            new LoggerConfiguration().CreateLogger());

        Assert.True(await client.ResumeAsync(CancellationToken.None));
        Assert.Equal(0, controls);
    }

    [Theory]
    [InlineData("pause", 0, true, false)]
    [InlineData("stop", 2, true, true)]
    public async Task ControlRequiresExactTerminalStateAcknowledgement(
        string action,
        int wireAction,
        bool paused,
        bool stopped)
    {
        var exact = new CoreAutopilotControlClient(
            (command, _, _) => Task.FromResult<IpcResponse?>(Ok(
                command,
                new { Action = wireAction, Applied = true, Paused = paused, Stopped = stopped })),
            new LoggerConfiguration().CreateLogger());
        var contradictory = new CoreAutopilotControlClient(
            (command, _, _) => Task.FromResult<IpcResponse?>(Ok(
                command,
                new { Action = wireAction, Applied = true, Paused = !paused, Stopped = stopped })),
            new LoggerConfiguration().CreateLogger());

        var exactApplied = action == "pause"
            ? await exact.PauseAsync(CancellationToken.None)
            : await exact.StopAsync(CancellationToken.None);
        var contradictoryApplied = action == "pause"
            ? await contradictory.PauseAsync(CancellationToken.None)
            : await contradictory.StopAsync(CancellationToken.None);

        Assert.True(exactApplied);
        Assert.False(contradictoryApplied);
    }

    private static IpcResponse Ok(string command, object payload) => new(
        "test-request",
        IpcStatus.Ok,
        command,
        JsonSerializer.SerializeToElement(payload),
        null);
}
