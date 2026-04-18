using SuavoAgent.Contracts.Reasoning;
using SuavoAgent.Core.Reasoning;

namespace SuavoAgent.Core.Tests.Reasoning;

/// <summary>
/// Deterministic test double for ILocalInference. Tests can enqueue canned
/// responses or set a null/error mode without standing up a real model.
/// </summary>
public sealed class MockLocalInference : ILocalInference
{
    public string ModelId => "mock";
    public bool IsReady { get; set; } = true;

    /// <summary>Canned proposals returned in order, FIFO. Null = return null.</summary>
    public Queue<InferenceProposal?> Responses { get; } = new();

    /// <summary>When true, ProposeAsync throws (tests defense-in-depth in TieredBrain).</summary>
    public bool ThrowOnPropose { get; set; }

    /// <summary>Artificial latency to simulate slow inference.</summary>
    public TimeSpan ArtificialLatency { get; set; } = TimeSpan.Zero;

    public int CallCount { get; private set; }

    public async Task<InferenceProposal?> ProposeAsync(InferenceRequest request, CancellationToken ct)
    {
        CallCount++;

        if (ArtificialLatency > TimeSpan.Zero)
            await Task.Delay(ArtificialLatency, ct);

        if (ThrowOnPropose)
            throw new InvalidOperationException("Mock configured to throw");

        return Responses.Count > 0 ? Responses.Dequeue() : null;
    }

    /// <summary>Convenience: queue a simple approved proposal.</summary>
    public void EnqueueApproved(RuleActionType type, double confidence,
        params (string Key, string Value)[] parameters)
    {
        var dict = parameters.ToDictionary(p => p.Key, p => p.Value);
        Responses.Enqueue(new InferenceProposal
        {
            Action = new RuleActionSpec
            {
                Type = type,
                Parameters = dict,
            },
            Confidence = confidence,
            ModelId = "mock",
            Rationale = "test",
            LatencyMs = 0,
        });
    }
}
