using System.Collections.Generic;
using SuavoAgent.Core.Agentic;
using SuavoAgent.Core.Agentic.Adapters;
using Xunit;

namespace SuavoAgent.Core.Tests.Agentic;

// The grounding layer: LLM freeform intent ("click Save") -> the CONCRETE perceived element's
// signature, so strict actuation verbs can execute it. This is the bridge that lets the same brain
// act on any app. Fail-closed: no perceived match -> null (never act on a hallucinated element).
public sealed class ActionGroundingTests
{
    private static PerceivedScreen Screen(params string[] sigs)
        => new("hash", Scrubbed: true, sigs, WindowTitle: "sim", Signatures: sigs);

    [Fact]
    public void GroundClick_resolves_automationId_hint_to_signature()
    {
        var g = ActionGrounding.GroundClick(
            new Dictionary<string, string> { ["element"] = "saveBtn" },
            Screen("Button|saveBtn", "MenuItem|fileMenu"), "notepad");

        Assert.NotNull(g);
        Assert.Equal("saveBtn", g!["automationId"]);
        Assert.Equal("Button", g["controlType"]);
        Assert.Equal("notepad", g["process_name"]);
    }

    [Fact]
    public void GroundClick_resolves_full_signature_hint()
    {
        var g = ActionGrounding.GroundClick(
            new Dictionary<string, string> { ["target"] = "Button|saveBtn" },
            Screen("Button|saveBtn"), "notepad");

        Assert.NotNull(g);
        Assert.Equal("saveBtn", g!["automationId"]);
        Assert.Equal("Button", g["controlType"]);
    }

    [Fact]
    public void GroundClick_resolves_loose_substring_reference()
    {
        // The LLM says "save"; the element is "saveBtn" — bind it.
        var g = ActionGrounding.GroundClick(
            new Dictionary<string, string> { ["label"] = "save" },
            Screen("Button|saveBtn"), "notepad");

        Assert.NotNull(g);
        Assert.Equal("saveBtn", g!["automationId"]);
    }

    [Fact]
    public void GroundClick_no_perceived_match_returns_null()
    {
        var g = ActionGrounding.GroundClick(
            new Dictionary<string, string> { ["element"] = "nonexistentThing" },
            Screen("Button|saveBtn"), "notepad");

        Assert.Null(g); // fail-closed — never act on a hallucinated element
    }

    [Fact]
    public void GroundClick_null_screen_returns_null()
        => Assert.Null(ActionGrounding.GroundClick(
            new Dictionary<string, string> { ["element"] = "saveBtn" }, null, "notepad"));

    [Fact]
    public void GroundClick_blank_process_returns_null()
        => Assert.Null(ActionGrounding.GroundClick(
            new Dictionary<string, string> { ["element"] = "saveBtn" }, Screen("Button|saveBtn"), ""));

    [Fact]
    public void GroundClick_picks_first_signature_on_tie()
    {
        // Two elements both loosely match "btn"; the first perceived wins (stable, deterministic).
        var g = ActionGrounding.GroundClick(
            new Dictionary<string, string> { ["element"] = "btn" },
            Screen("Button|saveBtn", "Button|cancelBtn"), "notepad");

        Assert.NotNull(g);
        Assert.Equal("saveBtn", g!["automationId"]);
    }
}
