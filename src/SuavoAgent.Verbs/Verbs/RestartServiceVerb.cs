using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace SuavoAgent.Verbs.Verbs;

/// <summary>
/// Universal LOW-risk verb. Restarts a named SuavoAgent Windows service via
/// sc.exe. Rollback is idempotent (service is either running or not) so the
/// envelope carries pre-state but the inverse is a no-op.
/// </summary>
public sealed class RestartServiceVerb : IVerb
{
    private static readonly HashSet<string> AllowedServices = new(StringComparer.OrdinalIgnoreCase)
    {
        "SuavoAgent.Core",
        "SuavoAgent.Broker",
        "SuavoAgent.Watchdog"
    };

    public VerbMetadata Metadata { get; } = new(
        Name: "restart_service",
        Version: "1.0.0",
        Description: "Restart a named SuavoAgent Windows service via sc.exe.",
        RiskTier: VerbRiskTier.Low,
        BaaScope: VerbBaaScope.NoneInstance,
        IsMutation: true,
        IsDestructive: false,
        MaxExecutionTime: TimeSpan.FromSeconds(90),
        Parameters: new VerbParameterSchema(new[]
        {
            new VerbParameterDefinition(
                Name: "service_name",
                ClrType: typeof(string),
                ValidationHint: "enum:SuavoAgent.Core|SuavoAgent.Broker|SuavoAgent.Watchdog")
        }),
        Output: new VerbOutputSchema(new[]
        {
            new VerbOutputField("final_state", typeof(string)),
            new VerbOutputField("duration_ms", typeof(long))
        }),
        BlastRadius: new VerbBlastRadius(
            ExpectedDollarsImpact: 0,
            PhiRecordsExposed: 0,
            DowntimeSeconds: 90,
            RecoverableWithinSeconds: 300,
            Justification: "Bounded — service either starts or doesn't. No PHI path. Core restart momentarily pauses SQL polling; resumes within 90s."),
        RequiresVerbs: Array.Empty<string>(),
        ConflictingVerbs: new[] { "restart_service", "apply_config_override" });

    public Task<VerbPreconditionResult> CheckPreconditionsAsync(VerbContext ctx)
    {
        if (!ctx.Parameters.TryGetValue("service_name", out var nameObj) || nameObj is not string name)
            return Task.FromResult(VerbPreconditionResult.Fail("missing_parameter: service_name"));

        if (!AllowedServices.Contains(name))
            return Task.FromResult(VerbPreconditionResult.Fail($"service_name '{name}' not in allowlist"));

        var controller = ctx.Services.GetRequiredService<IServiceController>();
        var state = controller.Query(name);
        if (state == ServiceState.NotInstalled)
            return Task.FromResult(VerbPreconditionResult.Fail("service_not_installed"));

        var evidence = new Dictionary<string, string>
        {
            ["pre_state"] = state.ToString()
        };
        return Task.FromResult(VerbPreconditionResult.Ok(evidence));
    }

    public Task<VerbRollbackEnvelope> CaptureRollbackAsync(VerbContext ctx)
    {
        var name = (string)ctx.Parameters["service_name"]!;
        var controller = ctx.Services.GetRequiredService<IServiceController>();
        var preState = controller.Query(name);

        var evidenceHash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{name}:{preState}"));

        // Idempotent service state: rollback is a no-op since restart brings
        // the service to Running regardless of prior state (or fails verifiably).
        var envelope = new VerbRollbackEnvelope(
            VerbInvocationId: ctx.InvocationId,
            InverseActionType: "noop_service_restart_is_idempotent",
            PreState: new Dictionary<string, object?> { ["service_name"] = name, ["state"] = preState.ToString() },
            InverseFn: _ => Task.CompletedTask,
            MaxInverseDuration: TimeSpan.FromSeconds(5),
            Evidence: Convert.ToHexString(evidenceHash).ToLowerInvariant());

        return Task.FromResult(envelope);
    }

    public async Task<VerbExecutionResult> ExecuteAsync(VerbContext ctx)
    {
        var name = (string)ctx.Parameters["service_name"]!;
        var controller = ctx.Services.GetRequiredService<IServiceController>();
        var stopwatch = Stopwatch.StartNew();

        var ok = controller.Start(name, ctx.CancellationToken.IsCancellationRequested
            ? TimeSpan.FromSeconds(1)
            : Metadata.MaxExecutionTime);

        stopwatch.Stop();

        if (!ok)
        {
            return VerbExecutionResult.Fail($"sc.exe start '{name}' failed or timed out", stopwatch.ElapsedMilliseconds);
        }

        // Poll for RUNNING up to MaxExecutionTime (minus already-elapsed).
        var deadline = DateTimeOffset.UtcNow.Add(Metadata.MaxExecutionTime - stopwatch.Elapsed);
        ServiceState finalState;
        do
        {
            finalState = controller.Query(name);
            if (finalState == ServiceState.Running) break;
            await Task.Delay(TimeSpan.FromSeconds(2), ctx.CancellationToken).ConfigureAwait(false);
        } while (DateTimeOffset.UtcNow < deadline && !ctx.CancellationToken.IsCancellationRequested);

        stopwatch.Stop();

        var output = new Dictionary<string, object?>
        {
            ["final_state"] = finalState.ToString(),
            ["duration_ms"] = stopwatch.ElapsedMilliseconds
        };

        return finalState == ServiceState.Running
            ? VerbExecutionResult.Ok(output, stopwatch.ElapsedMilliseconds)
            : VerbExecutionResult.Fail($"service did not reach Running (final: {finalState})", stopwatch.ElapsedMilliseconds);
    }

    public Task<VerbPostconditionResult> VerifyPostconditionsAsync(VerbContext ctx)
    {
        var name = (string)ctx.Parameters["service_name"]!;
        var controller = ctx.Services.GetRequiredService<IServiceController>();
        var state = controller.Query(name);

        if (state != ServiceState.Running)
            return Task.FromResult(VerbPostconditionResult.Fail($"service is in state '{state}', expected Running"));

        var evidence = new Dictionary<string, string>
        {
            ["post_state"] = state.ToString(),
            ["verified_at"] = DateTimeOffset.UtcNow.ToString("O")
        };
        return Task.FromResult(VerbPostconditionResult.Ok(evidence));
    }
}
