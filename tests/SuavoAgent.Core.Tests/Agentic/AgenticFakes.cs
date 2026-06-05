using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SuavoAgent.Core.Agentic;

namespace SuavoAgent.Core.Tests.Agentic;

/// <summary>
/// Controllable perceiver. Returns frames from an explicit queue; once exhausted it
/// repeats the last frame (so settle loops stabilize and main-perceive stays defined).
/// The same port serves the main perceive AND the settle polls — exactly as production.
/// </summary>
internal sealed class FakePerceiver : IPerceiver
{
    private readonly Queue<PerceivedScreen?> _frames;
    private PerceivedScreen? _last;
    public int Calls { get; private set; }

    public FakePerceiver(params PerceivedScreen?[] frames)
    {
        _frames = new Queue<PerceivedScreen?>(frames);
    }

    public Task<PerceivedScreen?> PerceiveAsync(CancellationToken ct)
    {
        Calls++;
        if (_frames.Count > 0)
            _last = _frames.Dequeue();
        return Task.FromResult(_last);
    }
}

internal sealed class FakeReasoner : IReasoner
{
    public Func<AgentObjective, WorkingMemory, IReadOnlySet<string>, bool, ReasonResult>? OnReason;
    public Func<AgentObjective, NextAction, PerceivedScreen, PerceivedScreen, bool, VerifyResult>? OnVerify;

    public int ReasonCalls { get; private set; }
    public int VerifyCalls { get; private set; }
    public readonly List<WorkingMemory> ReasonMemories = new();
    public readonly List<bool> VerifyAllowCloud = new();
    public PerceivedScreen? LastVerifyBefore { get; private set; }
    public PerceivedScreen? LastVerifyAfter { get; private set; }

    public Task<ReasonResult> ReasonNextAsync(
        AgentObjective objective, WorkingMemory memory, IReadOnlySet<string> allowedActions, bool allowCloud, CancellationToken ct)
    {
        ReasonCalls++;
        ReasonMemories.Add(memory);
        var result = OnReason?.Invoke(objective, memory, allowedActions, allowCloud)
                     ?? new ReasonResult(NextAction.Done, UsedCloud: false);
        return Task.FromResult(result);
    }

    public Task<VerifyResult> VerifyPostconditionAsync(
        AgentObjective objective, NextAction lastAction, PerceivedScreen before, PerceivedScreen after, bool allowCloud, CancellationToken ct)
    {
        VerifyCalls++;
        VerifyAllowCloud.Add(allowCloud);
        LastVerifyBefore = before;
        LastVerifyAfter = after;
        var result = OnVerify?.Invoke(objective, lastAction, before, after, allowCloud)
                     ?? new VerifyResult(PostconditionVerdict.Met, UsedCloud: false);
        return Task.FromResult(result);
    }
}

internal sealed class FakeActuator : IActuator
{
    public Func<NextAction, ActuationContext, ActOutcome>? OnAct;
    public readonly List<(NextAction Action, ActuationContext Ctx)> Calls = new();

    public Task<ActOutcome> ActAsync(NextAction action, ActuationContext ctx, CancellationToken ct)
    {
        Calls.Add((action, ctx));
        var outcome = OnAct?.Invoke(action, ctx) ?? new ActOutcome(ActStatus.Success, DryRun: ctx.DryRun);
        return Task.FromResult(outcome);
    }
}

internal sealed class FakeSafetyGate : ISafetyGate
{
    public Func<AgentObjective, SafetyVerdict> PreflightFn = _ => SafetyVerdict.Allow;
    public Func<NextAction, AgentObjective, SafetyVerdict> GateActionFn = (_, _) => SafetyVerdict.Allow;
    public Func<PerceivedScreen, bool> AssertScrubbedFn = s => s.Scrubbed;

    public int PreflightCalls { get; private set; }
    public int GateActionCalls { get; private set; }

    public SafetyVerdict Preflight(AgentObjective objective)
    {
        PreflightCalls++;
        return PreflightFn(objective);
    }

    public SafetyVerdict GateAction(NextAction action, AgentObjective objective)
    {
        GateActionCalls++;
        return GateActionFn(action, objective);
    }

    public bool AssertScrubbed(PerceivedScreen screen) => AssertScrubbedFn(screen);
}

/// <summary>Test helpers: a stable objective, a no-op delay, and a fixed clock.</summary>
internal static class AgenticTestKit
{
    public static readonly AgentObjective Objective = new("open notepad and type hi", "task.demo", "pharmacy.test");

    public static Func<TimeSpan, CancellationToken, Task> NoDelay => (_, _) => Task.CompletedTask;

    public static Func<DateTimeOffset> FixedClock(DateTimeOffset at) => () => at;

    public static PerceivedScreen Screen(string hash, bool scrubbed = true, string? title = null)
        => new(hash, scrubbed, new[] { $"el:{hash}" }, title);

    public static AgenticLoopRunner Runner(
        FakePerceiver perceiver, FakeReasoner reasoner, FakeActuator actuator, FakeSafetyGate safety,
        Func<DateTimeOffset>? utcNow = null)
        => new(perceiver, reasoner, actuator, safety, utcNow, NoDelay);
}
