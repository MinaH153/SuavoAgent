using System.Reflection;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Core.Learning;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Learning;

public sealed class DmvQueryObserverBoundaryTests : IDisposable
{
    private readonly AgentStateDb _db = new(":memory:");

    [Fact]
    public async Task StartWithoutDmvPermissionBecomesDormantWithoutThrowing()
    {
        var observer = Observer();

        await observer.StartAsync("session-dormant", CancellationToken.None);

        Assert.False(observer.HasDmvAccess);
        Assert.True(observer.CheckHealth().IsRunning);
        await observer.StopAsync();
        Assert.False(observer.CheckHealth().IsRunning);
        observer.Dispose();
    }

    [Fact]
    public async Task PollConnectionFailureReturnsFalseAndPreservesObservationState()
    {
        var observer = Observer();

        var result = await InvokeAsync<bool>(observer, "PollDmvAsync", "session-poll");

        Assert.False(result);
        Assert.Equal(0, observer.CheckHealth().EventsCollected);
    }

    [Fact]
    public async Task ClockCalibrationFailureResetsOffsetAndNotifiesSubscriber()
    {
        var observer = Observer();
        var notifications = new List<bool>();
        observer.ClockCalibratedChanged += notifications.Add;

        await InvokeAsync(observer, "CalibrateClockAsync");

        Assert.Equal(0, observer.ClockOffsetMs);
        Assert.Equal(new[] { false }, notifications);
    }

    [Theory]
    [InlineData(typeof(OperationCanceledException), true)]
    [InlineData(typeof(TaskCanceledException), true)]
    [InlineData(typeof(IOException), false)]
    public void TransientClassifierRecognizesOnlyCancellation(
        Type exceptionType,
        bool expected)
    {
        var exception = Assert.IsAssignableFrom<Exception>(
            Activator.CreateInstance(exceptionType));
        var method = typeof(DmvQueryObserver).GetMethod(
            "IsTransient", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("IsTransient missing.");

        Assert.Equal(expected, method.Invoke(null, [exception]));
    }

    public void Dispose() => _db.Dispose();

    private DmvQueryObserver Observer() => new(
        _db,
        () => new SqlConnection(),
        NullLogger<DmvQueryObserver>.Instance);

    private static async Task InvokeAsync(object target, string methodName, params object[] args)
    {
        var method = typeof(DmvQueryObserver).GetMethod(
            methodName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"{methodName} missing.");
        await Assert.IsAssignableFrom<Task>(method.Invoke(target, args));
    }

    private static async Task<T> InvokeAsync<T>(
        object target,
        string methodName,
        params object[] args)
    {
        var method = typeof(DmvQueryObserver).GetMethod(
            methodName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"{methodName} missing.");
        return await Assert.IsAssignableFrom<Task<T>>(method.Invoke(target, args));
    }
}
