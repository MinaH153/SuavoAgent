using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SuavoAgent.Contracts.Reasoning;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Reasoning;
using Xunit;

namespace SuavoAgent.Core.Tests.Reasoning;

public sealed class LLamaLocalInferenceBoundaryTests
{
    [Fact]
    public async Task EmptyActionSetReturnsNullWithoutLoadingAndCallerCancellationPropagates()
    {
        await using var inference = Create();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var empty = await inference.ProposeAsync(
            Request(new HashSet<RuleActionType>()), cancellation.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => inference.ProposeAsync(
                Request(new HashSet<RuleActionType> { RuleActionType.Log }),
                cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => inference.ChatAsync("hello", cancellation.Token));

        Assert.Null(empty);
        Assert.Equal("boundary-model", inference.ModelId);
        Assert.True(inference.IsReady);
        Assert.False(inference.LoadHasFailed);
    }

    [Theory]
    [InlineData("rejected")]
    [InlineData("exception")]
    [InlineData("missing_model")]
    public async Task RepeatedLoadAuthorizationOrAssetFailureBecomesObservable(string failure)
    {
        var verifierCalls = 0;
        Func<CancellationToken, Task<bool>> verifier = failure switch
        {
            "rejected" => _ =>
            {
                verifierCalls++;
                return Task.FromResult(false);
            },
            "exception" => _ =>
            {
                verifierCalls++;
                return Task.FromException<bool>(new IOException("cohort proof unavailable"));
            },
            _ => _ =>
            {
                verifierCalls++;
                return Task.FromResult(true);
            },
        };
        await using var inference = Create(verifier);

        Assert.Null(await inference.ChatAsync("first", CancellationToken.None));
        Assert.Null(await inference.ChatAsync("second", CancellationToken.None));

        Assert.Equal(2, verifierCalls);
        Assert.True(inference.LoadHasFailed);
    }

    [Fact]
    public async Task BlankChatIsRejectedBeforeModelAuthorization()
    {
        var verifierCalls = 0;
        await using var inference = Create(_ =>
        {
            verifierCalls++;
            return Task.FromResult(false);
        });

        Assert.Null(await inference.ChatAsync("  \t", CancellationToken.None));

        Assert.Equal(0, verifierCalls);
    }

    [Theory]
    [InlineData(ChatPromptFormat.Llama3, "<|start_header_id|>system")]
    [InlineData(ChatPromptFormat.Zephyr, "<|assistant|>")]
    [InlineData(ChatPromptFormat.Phi, "<|end|>")]
    [InlineData(ChatPromptFormat.ChatML, "<|im_start|>assistant")]
    [InlineData((ChatPromptFormat)999, "system boundary")]
    public void ChatPromptBuilderUsesExactModelFamilyEnvelope(
        ChatPromptFormat format,
        string expectedToken)
    {
        var prompt = LLamaLocalInference.BuildChatPrompt(
            "  user boundary  ", "system boundary", format);

        Assert.Contains(expectedToken, prompt);
        Assert.Contains("user boundary", prompt);
        Assert.DoesNotContain("  user boundary  ", prompt);
    }

    [Fact]
    public void ChatCapabilityPolicyNeverClaimsUnverifiedHardwareOrAppControl()
    {
        Assert.Equal(
            "I haven't verified this computer's hardware specifications.",
            CapabilityTruthPolicy.TryReply("tell me the specs of this computer"));
        Assert.Equal(
            "I haven't verified access to PioneerRx or computer-control authority for this request.",
            CapabilityTruthPolicy.TryReply("do you see and have access to PioneerRx"));
        Assert.Equal(
            "I haven't verified access to Calculator or computer-control authority for this request.",
            CapabilityTruthPolicy.TryReply("Can you open Calculator and type 2=2"));
        Assert.Null(CapabilityTruthPolicy.TryReply("Why did the pricing run stop?"));
    }

    [Fact]
    public void ChatSystemPromptRequiresEvidenceBeforeCapabilityClaims()
    {
        Assert.Contains("Only claim a capability when this request includes verified evidence", LLamaLocalInference.ChatSystemPrompt);
        Assert.DoesNotContain("You can see the screen", LLamaLocalInference.ChatSystemPrompt);
        Assert.DoesNotContain("control the mouse and keyboard", LLamaLocalInference.ChatSystemPrompt);
    }

    [Fact]
    public async Task IdleWatcherHonorsInFlightRecentUseAndAbsentWeightsBranches()
    {
        await using var inference = Create();
        var unload = typeof(LLamaLocalInference).GetMethod(
            "UnloadIfIdleAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("UnloadIfIdleAsync missing.");

        SetField(inference, "_activeInferences", 1);
        await InvokeTask(unload, inference, TimeSpan.Zero);
        SetField(inference, "_activeInferences", 0);
        SetField(inference, "_lastUse", DateTime.UtcNow);
        await InvokeTask(unload, inference, TimeSpan.FromHours(1));
        SetField(inference, "_lastUse", DateTime.MinValue);
        await InvokeTask(unload, inference, TimeSpan.Zero);

        Assert.False(inference.LoadHasFailed);
    }

    [Fact]
    public async Task RestartingIdleWatcherCancelsAndDisposesPreviousTimer()
    {
        var inference = Create();
        var restart = typeof(LLamaLocalInference).GetMethod(
            "RestartIdleWatcher", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("RestartIdleWatcher missing.");

        restart.Invoke(inference, null);
        var first = ReadField<CancellationTokenSource>(inference, "_idleWatcherCts");
        restart.Invoke(inference, null);
        var second = ReadField<CancellationTokenSource>(inference, "_idleWatcherCts");

        Assert.NotSame(first, second);
        Assert.True(first.IsCancellationRequested);
        await inference.DisposeAsync();
        await Task.Delay(25);
    }

    [Fact]
    public async Task LateFaultObserverConsumesDetachedInferenceException()
    {
        var observe = typeof(LLamaLocalInference).GetMethod(
            "ObserveLateFault", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ObserveLateFault missing.");
        var fault = Task.FromException(new InvalidOperationException("detached"));

        observe.Invoke(null, [fault]);
        await Task.Delay(25);

        Assert.True(fault.IsFaulted);
    }

    private static LLamaLocalInference Create(
        Func<CancellationToken, Task<bool>>? verifier = null) => new(
        Options.Create(new AgentOptions
        {
            Reasoning = new ReasoningOptions
            {
                Enabled = true,
                ModelId = "boundary-model",
                ContextSize = 256,
                MaxOutputTokens = 32,
                IdleUnloadSeconds = 10,
            },
        }),
        Path.Combine(Path.GetTempPath(), "missing-" + Guid.NewGuid().ToString("N") + ".gguf"),
        NullLogger<LLamaLocalInference>.Instance,
        verifier);

    private static InferenceRequest Request(IReadOnlySet<RuleActionType> actions) => new()
    {
        Context = new RuleContext
        {
            SkillId = "boundary",
            VisibleElements = new HashSet<string> { "Button:Open" },
        },
        EscalationReason = "boundary test",
        AllowedActions = actions,
    };

    private static async Task InvokeTask(MethodInfo method, object target, params object[] args) =>
        await Assert.IsAssignableFrom<Task>(method.Invoke(target, args));

    private static void SetField(object target, string name, object value)
    {
        var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Missing field {name}.");
        field.SetValue(target, value);
    }

    private static T ReadField<T>(object target, string name) where T : class
    {
        var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Missing field {name}.");
        return Assert.IsType<T>(field.GetValue(target));
    }
}
