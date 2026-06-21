// src/SuavoAgent.Setup/Verify/PostInstallVerifier.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SuavoAgent.Setup.Verify;

/// <summary>Runs all self-verify gates and produces a pass/fail outcome. Fail on any gate blocks Success.</summary>
public sealed class PostInstallVerifier
{
    private readonly IReadOnlyList<Func<CancellationToken, Task<GateResult>>> _gates;

    public PostInstallVerifier(IReadOnlyList<Func<CancellationToken, Task<GateResult>>> gates)
        => _gates = gates;

    public async Task<VerifyOutcome> RunAsync(CancellationToken ct)
    {
        var results = new List<GateResult>();
        foreach (var gate in _gates)
            results.Add(await gate(ct));
        var firstFail = results.FirstOrDefault(g => g.State == GateState.Fail);
        var passed = firstFail is null;
        var summary = passed ? "All checks passed." : $"{firstFail!.Name}: {firstFail.Detail}";
        return new VerifyOutcome(passed, results, summary);
    }

    public static string ToJson(VerifyOutcome outcome) => JsonSerializer.Serialize(new
    {
        passed = outcome.Passed,
        summary = outcome.Summary,
        gates = outcome.Gates.Select(g => new { name = g.Name, state = g.State.ToString(), detail = g.Detail }),
    }, new JsonSerializerOptions { WriteIndented = true });
}

public sealed record VerifyOutcome(bool Passed, IReadOnlyList<GateResult> Gates, string Summary);
