using SuavoAgent.Contracts.Ipc;
using Xunit;

namespace SuavoAgent.Helper.Tests;

/// <summary>
/// The dispatch wedge watchdog: the command server has one active pipe plus one pending listener
/// but only one sequential handler. A hung synchronous UIA/COM dispatch (hung PMS, torn-down
/// session) would strand the whole pipe FOREVER while the process looks alive — the "agent says
/// healthy but the cursor never moves" failure. Past the ceiling the Helper self-terminates and
/// the Broker relaunches it clean. These tests pin the guard mechanism with an injected
/// (non-exiting) wedge action.
/// </summary>
public class IpcCommandServerWedgeGuardTests
{
    private static IpcResponse Ok() => new("id", IpcStatus.Ok, "ping", null, null);

    [Fact]
    public async Task FastDispatch_PassesThrough_WedgeActionNotInvoked()
    {
        var wedged = 0;

        var (isWedged, response) = await IpcCommandServer.AwaitWithWedgeGuard(
            Task.FromResult(Ok()), TimeSpan.FromSeconds(5), () => wedged++);

        Assert.False(isWedged);
        Assert.NotNull(response);
        Assert.Equal(0, wedged);
    }

    [Fact]
    public async Task HungDispatch_FiresWedgeAction_ExactlyOnce()
    {
        var wedged = 0;
        var never = new TaskCompletionSource<IpcResponse>().Task; // a dispatch that never returns

        var (isWedged, response) = await IpcCommandServer.AwaitWithWedgeGuard(
            never, TimeSpan.FromMilliseconds(50), () => wedged++);

        Assert.True(isWedged);
        Assert.Null(response);
        Assert.Equal(1, wedged);
    }

    [Fact]
    public async Task SlowButAlive_InsideCeiling_IsNotWedged()
    {
        var wedged = 0;
        var slow = Task.Delay(50).ContinueWith(_ => Ok());

        var (isWedged, response) = await IpcCommandServer.AwaitWithWedgeGuard(
            slow, TimeSpan.FromSeconds(10), () => wedged++);

        Assert.False(isWedged);
        Assert.NotNull(response);
        Assert.Equal(0, wedged);
    }

    [Fact]
    public async Task FaultedDispatch_PropagatesException_WithoutWedgeAction()
    {
        // An exception is a NORMAL failure (HandleConnection logs and continues serving) —
        // only a HANG is a wedge. The guard must not swallow or misclassify it.
        var wedged = 0;
        var faulted = Task.FromException<IpcResponse>(new InvalidOperationException("dispatch bug"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            IpcCommandServer.AwaitWithWedgeGuard(faulted, TimeSpan.FromSeconds(5), () => wedged++));

        Assert.Equal(0, wedged);
    }

    [Fact]
    public void Ceiling_ComfortablyExceedsEveryLegitimateDispatch()
    {
        // Longest legitimate dispatches: find_file (Core waits 60s) and a slow UIA pricing
        // lookup (Core waits 30s). The ceiling must dominate them with wide margin so a
        // watchdog fire is ALWAYS a genuine wedge — never a slow-but-alive Helper.
        Assert.True(IpcCommandServer.DispatchWedgeCeiling >= TimeSpan.FromMinutes(4));
    }
}
