using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Reasoning;
using Xunit;

namespace SuavoAgent.Core.Tests.Reasoning;

public sealed class LLamaLocalInferenceActivationTests
{
    [Fact]
    public async Task Actual_model_load_is_blocked_when_last_moment_cohort_proof_rejects()
    {
        var verifierCalls = 0;
        var options = Options.Create(new AgentOptions
        {
            Reasoning = new ReasoningOptions
            {
                Enabled = true,
                ModelId = "test-model",
                ContextSize = 512,
                MaxOutputTokens = 64,
            },
        });
        await using var inference = new LLamaLocalInference(
            options,
            Path.Combine(Path.GetTempPath(), "must-not-load.gguf"),
            NullLogger<LLamaLocalInference>.Instance,
            _ =>
            {
                Interlocked.Increment(ref verifierCalls);
                return Task.FromResult(false);
            });

        var reply = await inference.ChatAsync("hello", CancellationToken.None);

        Assert.Null(reply);
        Assert.Equal(1, verifierCalls);
    }
}
