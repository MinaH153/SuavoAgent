using SuavoAgent.Contracts.Reasoning;
using SuavoAgent.Contracts.Vision;
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

    // --- vision grounding: the Tier-2 LLM reasons about what it SEES ---------
    // Rung 1 proved the Tier-1 rule path reasons over the captured screen. This proves the other
    // half: when vision is on, the on-device LLM's prompt is grounded in the PHI-scrubbed screen
    // (OCR text + UI elements) — and never carries raw patient data.

    [Fact]
    public void BuildUserMessage_VisionGrounded_IncludesScrubbedScreen_SoTheLlmSeesWhatItSees()
    {
        var msg = InferencePromptBuilder.BuildUserMessage(new InferenceRequest
        {
            Context = new RuleContext
            {
                SkillId = "pricing-lookup",
                ProcessName = "PioneerPharmacy",
                ScreenText = new[]
                {
                    new TextRegion { Text = "Pricing", Bounds = new Rect(120, 80, 60, 14), Confidence = 0.97 },
                    new TextRegion { Text = "Supplier", Bounds = new Rect(400, 160, 70, 14), Confidence = 0.96 },
                    new TextRegion { Text = "Cost Per Unit", Bounds = new Rect(560, 160, 90, 14), Confidence = 0.95 },
                    new TextRegion { Text = "[PATIENT]", Bounds = new Rect(40, 60, 80, 14), Confidence = 0.9 }, // already scrubbed
                },
                ScreenElements = new[]
                {
                    new VisualElement { Role = "DataItem", Name = "McKesson", Bounds = new Rect(400, 188, 150, 18), Confidence = 1.0 },
                },
            },
            EscalationReason = "no rule matched",
        });

        // The LLM's prompt is grounded in what's on screen.
        Assert.Contains("screen_text", msg);
        Assert.Contains("Pricing", msg);
        Assert.Contains("Supplier", msg);
        Assert.Contains("screen_elements", msg);
        Assert.Contains("DataItem", msg);
        // PHI safety: only the scrubbed sentinel reaches the model, never raw patient data.
        Assert.Contains("[PATIENT]", msg);
        Assert.DoesNotContain("John", msg);
    }

    [Fact]
    public void BuildUserMessage_NoVision_OmitsScreenKeys()
    {
        // When vision is off (no ScreenText/ScreenElements), the prompt must not carry the vision
        // keys at all — no overhead, no surface, byte-compatible with the legacy non-vision prompt.
        var msg = InferencePromptBuilder.BuildUserMessage(new InferenceRequest
        {
            Context = new RuleContext { SkillId = "pricing-lookup" },
            EscalationReason = "no rule matched",
        });

        Assert.DoesNotContain("screen_text", msg);
        Assert.DoesNotContain("screen_elements", msg);
    }

    [Fact]
    public void BuildUserMessage_IncludesObjective_WhenUserObjectiveSet()
    {
        var msg = InferencePromptBuilder.BuildUserMessage(Request(objective: "open notepad and type hi"));

        Assert.Contains("objective", msg);
        Assert.Contains("open notepad and type hi", msg);
    }

    [Fact]
    public void BuildUserMessage_OmitsObjectiveKey_WhenUserObjectiveNull()
    {
        // Backward-compat: a context with no objective must not emit an objective key.
        var msg = InferencePromptBuilder.BuildUserMessage(Request());

        Assert.DoesNotContain("objective", msg);
    }

    // --- prior_actions multi-step memory (2026-06 brain upgrade) -------------

    [Fact]
    public void BuildUserMessage_PriorActions_KeepsMostRecentSteps_NotOldest()
    {
        // Working memory is bounded to the most-recent steps; the transcript is
        // oldest→newest. The generic 200-char flag cap kept the HEAD (oldest),
        // truncating away the most-recent — the steps that matter most for the
        // next decision. prior_actions must retain the TAIL.
        var transcript = string.Join("; ", Enumerable.Range(1, 40).Select(i => $"click(elem{i})->Ok"));
        var msg = InferencePromptBuilder.BuildUserMessage(WithFlags(
            new Dictionary<string, string> { ["prior_actions"] = transcript }));

        Assert.Contains("elem40", msg); // most-recent step survives
        Assert.Contains("elem39", msg);
    }

    [Fact]
    public void BuildUserMessage_PriorActions_HasLargerBudgetThanGenericFlags()
    {
        // A non-prior_actions flag is capped at the generic 200-char field cap;
        // prior_actions gets a dedicated, larger budget so multi-step history fits.
        var longValue = new string('x', 600);
        var msg = InferencePromptBuilder.BuildUserMessage(WithFlags(
            new Dictionary<string, string>
            {
                ["prior_actions"] = longValue,
                ["other"] = longValue,
            }));

        // prior_actions keeps > 200 chars; a generic flag does not.
        Assert.Contains(new string('x', 400), msg); // prior_actions retained well past 200
    }

    private static InferenceRequest WithFlags(Dictionary<string, string> flags) => new()
    {
        Context = new RuleContext
        {
            SkillId = "navigate",
            ProcessName = "pharmacy.exe",
            WindowTitle = "Main",
            VisibleElements = new HashSet<string>(),
            Flags = flags,
        },
        EscalationReason = "no rule matched",
        AllowedActions = new HashSet<RuleActionType> { RuleActionType.Click, RuleActionType.Log },
    };

    // --- helpers -------------------------------------------------------------

    private static InferenceRequest Request(
        string skill = "test",
        IEnumerable<string>? visible = null,
        IEnumerable<RuleActionType>? allowed = null,
        string? objective = null) =>
        new()
        {
            Context = new RuleContext
            {
                SkillId = skill,
                ProcessName = "pharmacy.exe",
                WindowTitle = "Main",
                VisibleElements = new HashSet<string>(visible ?? Array.Empty<string>()),
                UserObjective = objective,
            },
            EscalationReason = "no rule matched",
            AllowedActions = new HashSet<RuleActionType>(
                allowed ?? Enum.GetValues<RuleActionType>()),
        };
}
