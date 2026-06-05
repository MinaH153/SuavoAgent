using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SuavoAgent.Core.ActionGrammarV1;
using SuavoAgent.Core.Audit;
using SuavoAgent.Core.Mission;

namespace SuavoAgent.Core.Agentic.Adapters;

/// <summary>Per-run actuation envelope the navigate command supplies (charter, audit chain, deadline).</summary>
public sealed record ActuationEnvelope(MissionCharter Charter, AuditChain Audit, string InvocationId, DateTimeOffset DeadlineUtc);

/// <summary>
/// Production <see cref="IActuator"/>. Resolves the NextAction's verb via <see cref="VerbRegistry"/>
/// and runs it through <see cref="VerbDispatcher.DispatchAsync"/> — inheriting its fail-closed 7-step
/// flow (param/authz/precond/rollback/exec/postcond/audit) → IActuationGateway → Helper under the
/// authoritative ActuationGate. The interesting result translation lives in pure
/// <see cref="NavigateActuation.MapResult"/>.
/// </summary>
public sealed class VerbActuator : IActuator
{
    private readonly VerbRegistry _registry;
    private readonly VerbDispatcher _dispatcher;
    private readonly IServiceProvider _services;
    private readonly Func<ActuationContext, ActuationEnvelope> _envelope;

    public VerbActuator(
        VerbRegistry registry,
        VerbDispatcher dispatcher,
        IServiceProvider services,
        Func<ActuationContext, ActuationEnvelope> envelope)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _envelope = envelope ?? throw new ArgumentNullException(nameof(envelope));
    }

    public async Task<ActOutcome> ActAsync(NextAction action, ActuationContext ctx, CancellationToken ct)
    {
        if (action.Kind != NextActionKind.Act || string.IsNullOrEmpty(action.Verb))
            return new ActOutcome(ActStatus.Failed, "not_an_actuating_action");

        var entry = _registry.Resolve(action.Verb, null, out var failureReason);
        if (entry is null)
            return new ActOutcome(ActStatus.Failed, failureReason ?? "verb_not_found");

        var verb = _registry.Instantiate(entry, _services);
        var env = _envelope(ctx);
        var parameters = action.Parameters is { } p
            ? p.ToDictionary(kv => kv.Key, kv => kv.Value)
            : new Dictionary<string, object?>();

        var verbCtx = new VerbContext(
            ctx.PharmacyId, env.Charter, env.Audit, env.InvocationId, "navigate",
            parameters, _services, env.DeadlineUtc, ctx.DryRun);

        var result = await _dispatcher.DispatchAsync(verb, verbCtx, ct).ConfigureAwait(false);
        return NavigateActuation.MapResult(result);
    }
}

/// <summary>Pure translation of a verb-dispatch result into the loop's ActOutcome.</summary>
public static class NavigateActuation
{
    public static ActOutcome MapResult(VerbDispatchResult result)
    {
        var status = result.Outcome switch
        {
            VerbDispatchOutcome.Success => ActStatus.Success,
            VerbDispatchOutcome.Rejected => ActStatus.Rejected,
            _ => ActStatus.Failed,
        };

        var rejectionCode = status == ActStatus.Success ? null : result.FailureReason;
        var evidenceHash = result.Output.TryGetValue("evidence_hash", out var eh) ? eh as string : null;
        var dryRun = result.Output.TryGetValue("dry_run", out var dr) && dr is true;

        return new ActOutcome(status, rejectionCode, evidenceHash, dryRun);
    }
}
