using Microsoft.Extensions.DependencyInjection;
using SuavoAgent.Core.Adapters;

namespace SuavoAgent.Core.ActionGrammarV1.Verbs.LookupPatient;

/// <summary>
/// Resolve a patient identifier (scoped, salted on our side) to the PMS-native
/// patient id a subsequent Rx-history verb can use. LOW risk — read-only.
/// AgentBaa scope: minimal PMS query, no PHI direct in parameters.
/// </summary>
public sealed class LookupPatientVerb : IVerb
{
    public const string VerbName = "lookup_patient";
    public const string VerbVersion = "1.0.0";

    public VerbMetadata Metadata { get; } = new(
        Name: VerbName,
        Version: VerbVersion,
        Description: "Resolve scoped patient identifier to PMS-native patient id",
        RiskTier: VerbRiskTier.Low,
        BaaScope: new VerbBaaScope.AgentBaa(),
        IsMutation: false,
        IsDestructive: false,
        MaxExecutionTime: TimeSpan.FromSeconds(15),
        Params: new VerbParameterSchema(new[]
        {
            new VerbParameterSpec("patient_identifier", typeof(string), Required: true,
                ValidationHint: "non-empty, scoped identifier (salted hash when operator-provided)")
        }),
        Output: new VerbOutputSchema(new[]
        {
            new VerbOutputSpec("patient_id", typeof(string)),
            new VerbOutputSpec("display_name_hash", typeof(string)),
            new VerbOutputSpec("last_activity_utc", typeof(DateTimeOffset)),
        }),
        BlastRadius: new VerbBlastRadius(
            ExpectedDollarsImpact: 0m,
            PhiRecordsExposed: 0,
            DowntimeSeconds: 0,
            RecoverableWithinSeconds: 0,
            Justification: "Read-only PMS query; no writes, no on-box mutation")
    );

    public Task<VerbPreconditionResult> CheckPreconditionsAsync(VerbContext ctx, CancellationToken ct)
    {
        if (!ctx.Parameters.TryGetValue("patient_identifier", out var raw)
            || raw is not string s
            || string.IsNullOrWhiteSpace(s))
        {
            return Task.FromResult(VerbPreconditionResult.Fail(
                "patient_identifier_non_empty",
                "patient_identifier parameter must be a non-empty string"));
        }

        var adapter = ctx.Services.GetService<IPharmacyReadAdapter>();
        if (adapter is null)
        {
            return Task.FromResult(VerbPreconditionResult.Fail(
                "adapter_missing",
                "IPharmacyReadAdapter not registered in the service provider"));
        }

        return Task.FromResult(VerbPreconditionResult.Ok());
    }

    public Task<VerbRollbackEnvelope> CaptureRollbackAsync(VerbContext ctx, CancellationToken ct) =>
        Task.FromResult(VerbRollbackEnvelope.None(ctx.InvocationId));

    public async Task<VerbExecutionResult> ExecuteAsync(VerbContext ctx, CancellationToken ct)
    {
        var adapter = ctx.Services.GetRequiredService<IPharmacyReadAdapter>();
        var identifier = (string)ctx.Parameters["patient_identifier"]!;

        var record = await adapter.LookupPatientAsync(identifier, ct).ConfigureAwait(false);
        if (record is null)
        {
            return VerbExecutionResult.Fail($"patient not found for identifier='{identifier}' on adapter='{adapter.AdapterType}'");
        }

        return VerbExecutionResult.Ok(new Dictionary<string, object?>
        {
            ["patient_id"] = record.PatientId,
            ["display_name_hash"] = record.DisplayNameHash,
            ["last_activity_utc"] = record.LastActivityUtc,
        });
    }

    public Task<VerbPostconditionResult> VerifyPostconditionsAsync(
        VerbContext ctx,
        VerbExecutionResult executionResult,
        CancellationToken ct)
    {
        if (!executionResult.Output.TryGetValue("patient_id", out var pid) || pid is not string s || string.IsNullOrWhiteSpace(s))
        {
            return Task.FromResult(VerbPostconditionResult.Fail(
                "patient_id_non_empty",
                "executed verb must return a non-empty patient_id"));
        }
        return Task.FromResult(VerbPostconditionResult.Ok());
    }
}
