using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Core.Workers;
using Xunit;

namespace SuavoAgent.Core.Tests.Workers;

/// <summary>
/// The worker supervisor: a faulted worker must restart in-process (bounded backoff) instead of
/// dying silently while the Windows service still reports "Running" — the Nadim 2026-04-25
/// "Running but dead for 12 days" failure mode. After MaxRestarts it escalates (cloud repair).
/// </summary>
public class ResilientHostedServiceTests
{
    private sealed class FaultyWorker : ResilientHostedService
    {
        private readonly int _throwTimes;
        private readonly int _max;
        private readonly bool _blockWhenHealthy;
        private int _runs;

        public int Runs => _runs;

        public FaultyWorker(int throwTimes, int maxRestarts, bool blockWhenHealthy)
            : base(NullLogger.Instance)
        {
            _throwTimes = throwTimes;
            _max = maxRestarts;
            _blockWhenHealthy = blockWhenHealthy;
        }

        protected override string WorkerName => "faulty";
        protected override int MaxRestarts => _max;
        protected override TimeSpan BackoffFor(int restart) => TimeSpan.Zero;

        protected override async Task RunAsync(CancellationToken ct)
        {
            _runs++;
            if (_runs <= _throwTimes)
                throw new InvalidOperationException("boom");
            if (_blockWhenHealthy)
                await Task.Delay(Timeout.Infinite, ct); // simulate a healthy long-running loop
        }
    }

    private static async Task WaitUntil(Func<bool> cond, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (!cond() && sw.ElapsedMilliseconds < timeoutMs)
            await Task.Delay(10);
        if (!cond())
            throw new TimeoutException("condition not met in time");
    }

    [Fact]
    public async Task RestartsAfterFaults_ThenStaysHealthy_NoEscalation()
    {
        var w = new FaultyWorker(throwTimes: 2, maxRestarts: 5, blockWhenHealthy: true);
        using var cts = new CancellationTokenSource();

        var task = w.RunSupervisedAsync(cts.Token);
        await WaitUntil(() => w.Runs >= 3, 2000); // 2 faults + 1 healthy (blocking) run
        cts.Cancel();
        await task;

        Assert.Equal(2, w.RestartCount);
        Assert.False(w.Escalated);
    }

    [Fact]
    public async Task ExceedsMaxRestarts_Escalates()
    {
        var w = new FaultyWorker(throwTimes: 1000, maxRestarts: 2, blockWhenHealthy: false);

        await w.RunSupervisedAsync(CancellationToken.None);

        Assert.True(w.Escalated);
        Assert.Equal(3, w.RestartCount); // faults at runs 1,2,3 → restart 1,2 ok, 3 > max → escalate
    }

    [Fact]
    public async Task CleanReturn_NoRestart()
    {
        var w = new FaultyWorker(throwTimes: 0, maxRestarts: 5, blockWhenHealthy: false);

        await w.RunSupervisedAsync(CancellationToken.None);

        Assert.Equal(0, w.RestartCount);
        Assert.False(w.Escalated);
    }

    [Fact]
    public async Task Cancellation_Stops_NoRestartNoEscalation()
    {
        var w = new FaultyWorker(throwTimes: 0, maxRestarts: 5, blockWhenHealthy: true);
        using var cts = new CancellationTokenSource();

        var task = w.RunSupervisedAsync(cts.Token);
        await WaitUntil(() => w.Runs >= 1, 2000);
        cts.Cancel();
        await task;

        Assert.Equal(0, w.RestartCount);
        Assert.False(w.Escalated);
    }
}
