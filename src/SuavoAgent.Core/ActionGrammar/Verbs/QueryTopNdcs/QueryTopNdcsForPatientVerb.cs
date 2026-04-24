using Microsoft.Extensions.DependencyInjection;
using SuavoAgent.Core.Adapters;

namespace SuavoAgent.Core.ActionGrammarV1.Verbs.QueryTopNdcs;

/// <summary>
/// Return the top-N NDCs for a given patient's Rx history. Output is the
/// NDC code list only — patient name, medication name, and free-text fields
/// are excluded at the adapter boundary per invariants §I.1.2.
/// </summary>
public sealed class QueryTopNdcsForPatientVerb : IVerb
{
    public const string VerbName = "query_top_ndcs_for_patient";
    public const string VerbVersion = "1.0.0";

    public VerbMetadata Metadata { get; } = new(
        Name: VerbName,
        Version: VerbVersion,
        Description: "Return top-N NDCs from a patient's Rx history (NDC only, no medication names)",
        RiskTier: VerbRiskTier.Low,
        BaaScope: new VerbBaaScope.AgentBaa(),
        IsMutation: false,
        IsDestructive: false,
        MaxExecutionTime: TimeSpan.FromSeconds(20),
        Params: new VerbParameterSchema(new[]
        {
            new VerbParameterSpec("patient_id", typeof(string), Required: true),
            new VerbParameterSpec("top_n", typeof(int), Required: true,
                ValidationHint: "1..50 inclusive"),
        }),
        Output: new VerbOutputSchema(new[]
        {
            new VerbOutputSpec("ndcs", typeof(IReadOnlyList<RxHistoryRecord>)),
            new VerbOutputSpec("result_count", typeof(int)),
        }),
        BlastRadius: new VerbBlastRadius(
            ExpectedDollarsImpact: 0m,
            PhiRecordsExposed: 0,
            DowntimeSeconds: 0,
            RecoverableWithinSeconds: 0,
            Justification: "Read-only aggregated NDC query; adapter strips PHI-direct fields before returning")
    );

    public Task<VerbPreconditionResult> CheckPreconditionsAsync(VerbContext ctx, CancellationToken ct)
    {
        if (!ctx.Parameters.TryGetValue("patient_id", out var pidRaw)
            || pidRaw is not string pid
            || string.IsNullOrWhiteSpace(pid))
        {
            return Task.FromResult(VerbPreconditionResult.Fail(
                "patient_id_non_empty",
                "patient_id parameter must be a non-empty string"));
        }

        if (!ctx.Parameters.TryGetValue("top_n", out var nRaw) || nRaw is not int n)
        {
            return Task.FromResult(VerbPreconditionResult.Fail(
                "top_n_integer",
                "top_n parameter must be an integer"));
        }

        if (n <= 0 || n > 50)
        {
            return Task.FromResult(VerbPreconditionResult.Fail(
                "top_n_range",
                $"top_n must be 1..50 inclusive, got {n}"));
        }

        if (ctx.Services.GetService<IPharmacyReadAdapter>() is null)
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
        var patientId = (string)ctx.Parameters["patient_id"]!;
        var topN = (int)ctx.Parameters["top_n"]!;

        var rows = await adapter.GetTopNdcsForPatientAsync(patientId, topN, ct).ConfigureAwait(false);
        if (rows is null)
        {
            return VerbExecutionResult.Fail($"adapter '{adapter.AdapterType}' returned null Rx history");
        }

        return VerbExecutionResult.Ok(new Dictionary<string, object?>
        {
            ["ndcs"] = rows,
            ["result_count"] = rows.Count,
        });
    }

    public Task<VerbPostconditionResult> VerifyPostconditionsAsync(
        VerbContext ctx,
        VerbExecutionResult executionResult,
        CancellationToken ct)
    {
        if (!executionResult.Output.TryGetValue("ndcs", out var ndcsRaw) ||
            ndcsRaw is not IReadOnlyList<RxHistoryRecord> ndcs)
        {
            return Task.FromResult(VerbPostconditionResult.Fail(
                "ndcs_shape",
                "executed verb must return an IReadOnlyList<RxHistoryRecord> under 'ndcs'"));
        }

        var requestedTopN = (int)ctx.Parameters["top_n"]!;
        if (ndcs.Count > requestedTopN)
        {
            return Task.FromResult(VerbPostconditionResult.Fail(
                "ndcs_cardinality",
                $"adapter returned {ndcs.Count} rows for top_n={requestedTopN}; postcondition requires result count <= top_n"));
        }

        foreach (var row in ndcs)
        {
            if (string.IsNullOrWhiteSpace(row.Ndc))
            {
                return Task.FromResult(VerbPostconditionResult.Fail(
                    "ndc_non_empty",
                    "adapter returned a row with empty NDC"));
            }
        }

        return Task.FromResult(VerbPostconditionResult.Ok());
    }
}
