using SuavoAgent.Contracts.Reasoning;
using SuavoAgent.Core.Reasoning;
using Xunit;

namespace SuavoAgent.Core.Tests.Reasoning;

public class InferencePromptBuilderTests
{
    [Fact]
    public void Build_IncludesLlamaChatTemplate()
    {
        var prompt = InferencePromptBuilder.Build(Request());

        Assert.Contains("<|begin_of_text|>", prompt);
        Assert.Contains("<|start_header_id|>system<|end_header_id|>", prompt);
        Assert.Contains("<|start_header_id|>user<|end_header_id|>", prompt);
        Assert.Contains("<|start_header_id|>assistant<|end_header_id|>", prompt);
        Assert.Contains("<|eot_id|>", prompt);
    }

    [Fact]
    public void BuildUserMessage_IncludesStateSkillAndAllowedActions()
    {
        var msg = InferencePromptBuilder.BuildUserMessage(Request(
            skill: "pricing-lookup",
            visible: new[] { "Save", "Cancel" }));

        Assert.Contains("pricing-lookup", msg);
        Assert.Contains("Save", msg);
        Assert.Contains("Cancel", msg);
        Assert.Contains("allowed_actions", msg);
    }

    [Fact]
    public void BuildUserMessage_CapsVisibleElementsAt24()
    {
        var many = Enumerable.Range(0, 50).Select(i => $"elem{i}").ToArray();
        var msg = InferencePromptBuilder.BuildUserMessage(Request(visible: many));

        Assert.Contains("elem0", msg);
        Assert.Contains("elem23", msg);
        Assert.DoesNotContain("elem24", msg);
        Assert.DoesNotContain("elem49", msg);
    }

    [Fact]
    public void BuildUserMessage_AllowedActionsSortedDeterministically()
    {
        var unordered = new HashSet<RuleActionType>
        {
            RuleActionType.PressKey,
            RuleActionType.Click,
            RuleActionType.Log,
        };

        var a = InferencePromptBuilder.BuildUserMessage(Request(allowed: unordered));
        var b = InferencePromptBuilder.BuildUserMessage(Request(allowed: unordered));

        Assert.Equal(a, b); // stable = cacheable in future
    }

    [Fact]
    public void BuildUserMessage_IncludesEscalationReason()
    {
        var msg = InferencePromptBuilder.BuildUserMessage(new InferenceRequest
        {
            Context = new RuleContext { SkillId = "s" },
            EscalationReason = "no rule matched visible state",
        });

        Assert.Contains("escalation_reason", msg);
        Assert.Contains("no rule matched", msg);
    }

    [Fact]
    public void BuildUserMessage_NoPatientPHI_OnlyScrubbedFieldsSerialized()
    {
        // Sanity: the builder serializes only the documented RuleContext fields.
        // Nothing in the schema carries patient data.
        var msg = InferencePromptBuilder.BuildUserMessage(Request());
        Assert.DoesNotContain("patient", msg);
        Assert.DoesNotContain("rx_number", msg);
        Assert.DoesNotContain("medication", msg);
    }

    // --- helpers -------------------------------------------------------------

    private static InferenceRequest Request(
        string skill = "test",
        IEnumerable<string>? visible = null,
        IEnumerable<RuleActionType>? allowed = null) =>
        new()
        {
            Context = new RuleContext
            {
                SkillId = skill,
                ProcessName = "pharmacy.exe",
                WindowTitle = "Main",
                VisibleElements = new HashSet<string>(visible ?? Array.Empty<string>()),
            },
            EscalationReason = "no rule matched",
            AllowedActions = new HashSet<RuleActionType>(
                allowed ?? Enum.GetValues<RuleActionType>()),
        };
}
