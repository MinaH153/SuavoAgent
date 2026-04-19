using SuavoAgent.Contracts.Reasoning;
using SuavoAgent.Core.Reasoning;

namespace SuavoAgent.Core.Tests.Reasoning;

/// <summary>
/// Test double for ICloudReasoning. FIFO queue of canned responses, plus a
/// counter that lets tests assert how many times Tier-3 was consulted.
/// </summary>
public sealed class MockCloudReasoning : ICloudReasoning
{
    public bool IsEnabled { get; set; } = true;
    public Queue<InferenceProposal?> Responses { get; } = new();
    public bool ThrowOnPropose { get; set; }
    public int CallCount { get; private set; }
    public string? LastTier2Reason { get; private set; }

    public Task<InferenceProposal?> ProposeAsync(
        InferenceRequest request,
        string tier2EscalationReason,
        CancellationToken ct)
    {
        CallCount++;
        LastTier2Reason = tier2EscalationReason;

        if (ThrowOnPropose)
            throw new InvalidOperationException("Mock cloud configured to throw");

        return Task.FromResult(Responses.Count > 0 ? Responses.Dequeue() : null);
    }

    public void EnqueueApproved(RuleActionType type, double confidence,
        params (string Key, string Value)[] parameters)
    {
        var dict = parameters.ToDictionary(p => p.Key, p => p.Value);
        Responses.Enqueue(new InferenceProposal
        {
            Action = new RuleActionSpec { Type = type, Parameters = dict },
            Confidence = confidence,
            ModelId = "mock-cloud",
            Rationale = "test-cloud",
            LatencyMs = 42,
        });
    }
}
