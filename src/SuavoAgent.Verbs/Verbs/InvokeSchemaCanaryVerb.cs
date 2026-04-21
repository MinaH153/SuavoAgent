using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace SuavoAgent.Verbs.Verbs;

/// <summary>
/// Universal LOW-risk verb. Triggers an immediate Schema Canary scan against
/// the PMS SQL schema. Read-only operation — no rollback envelope needed.
/// </summary>
/// <remarks>
/// The actual canary implementation lives in SuavoAgent.Core (Canary
/// namespace). This verb is the signed-invocation wrapper that lets cloud
/// dispatch a canary run on demand (e.g., after a PMS upgrade heuristic
/// fires in the cloud-side dispatcher).
/// </remarks>
public sealed class InvokeSchemaCanaryVerb : IVerb
{
    public VerbMetadata Metadata { get; } = new(
        Name: "invoke_schema_canary",
        Version: "1.0.0",
        Description: "Immediately run Schema Canary scan against the PMS SQL schema. Read-only.",
        RiskTier: VerbRiskTier.Low,
        BaaScope: VerbBaaScope.AgentBaaInstance,
        IsMutation: false,
        IsDestructive: false,
        MaxExecutionTime: TimeSpan.FromSeconds(60),
        Parameters: VerbParameterSchema.Empty,
        Output: new VerbOutputSchema(new[]
        {
            new VerbOutputField("canary_result", typeof(string)),
            new VerbOutputField("schema_hash", typeof(string)),
            new VerbOutputField("columns_observed", typeof(int)),
            new VerbOutputField("duration_ms", typeof(long))
        }),
        BlastRadius: new VerbBlastRadius(
            ExpectedDollarsImpact: 0,
            PhiRecordsExposed: 0,
            DowntimeSeconds: 0,
            RecoverableWithinSeconds: 0,
            Justification: "Read-only SELECT against schema catalog. No PHI paths. No pharmacy disruption."),
        RequiresVerbs: Array.Empty<string>(),
        ConflictingVerbs: new[] { "invoke_schema_canary" });

    public Task<VerbPreconditionResult> CheckPreconditionsAsync(VerbContext ctx)
    {
        var runner = ctx.Services.GetService<ISchemaCanaryRunner>();
        if (runner is null)
            return Task.FromResult(VerbPreconditionResult.Fail("schema_canary_runner_not_available"));

        if (!runner.IsReady)
            return Task.FromResult(VerbPreconditionResult.Fail("schema_canary_runner_not_ready (likely SQL connection unavailable)"));

        return Task.FromResult(VerbPreconditionResult.Ok());
    }

    public Task<VerbRollbackEnvelope> CaptureRollbackAsync(VerbContext ctx)
    {
        // Read-only verb → empty pre-state, no-op rollback.
        return Task.FromResult(new VerbRollbackEnvelope(
            VerbInvocationId: ctx.InvocationId,
            InverseActionType: "noop_read_only",
            PreState: new Dictionary<string, object?>(),
            InverseFn: _ => Task.CompletedTask,
            MaxInverseDuration: TimeSpan.FromSeconds(1),
            Evidence: ""));
    }

    public async Task<VerbExecutionResult> ExecuteAsync(VerbContext ctx)
    {
        var runner = ctx.Services.GetRequiredService<ISchemaCanaryRunner>();
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var result = await runner.RunOnceAsync(ctx.CancellationToken).ConfigureAwait(false);
            stopwatch.Stop();

            var output = new Dictionary<string, object?>
            {
                ["canary_result"] = result.Status,
                ["schema_hash"] = result.SchemaHash,
                ["columns_observed"] = result.ColumnsObserved,
                ["duration_ms"] = stopwatch.ElapsedMilliseconds
            };

            return VerbExecutionResult.Ok(output, stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return VerbExecutionResult.Fail(ex.Message, stopwatch.ElapsedMilliseconds);
        }
    }

    public Task<VerbPostconditionResult> VerifyPostconditionsAsync(VerbContext ctx)
    {
        // Postcondition for a read-only verb: the runner emitted a result.
        // Execution success captured in ExecuteAsync; no further check.
        return Task.FromResult(VerbPostconditionResult.Ok());
    }
}

/// <summary>
/// Abstraction over the Schema Canary subsystem in SuavoAgent.Core.
/// Implemented in Core; injected into the verb via DI.
/// </summary>
public interface ISchemaCanaryRunner
{
    bool IsReady { get; }
    Task<SchemaCanaryResult> RunOnceAsync(CancellationToken cancellationToken);
}

public sealed record SchemaCanaryResult(
    string Status,         // "green" | "yellow" | "red"
    string SchemaHash,     // SHA-256 of observed schema
    int ColumnsObserved);
