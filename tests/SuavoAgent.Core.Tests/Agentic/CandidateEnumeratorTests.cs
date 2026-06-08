using System.Collections.Generic;
using System.Linq;
using SuavoAgent.Core.Agentic;
using Xunit;

namespace SuavoAgent.Core.Tests.Agentic;

/// <summary>
/// Pins the frontier candidate generator: from a scrubbed screen it produces EXECUTABLE
/// click_by_label (from role:name element lines) + click_by_signature (from structural signatures)
/// actions, all bound to the sandbox process. Each candidate's ActionSig MUST equal the signature of
/// the NextAction it would emit, so a recorded EdgeKey lines up with an enumerated candidate across runs.
/// </summary>
public class CandidateEnumeratorTests
{
    private static readonly IReadOnlySet<string> ClickAllowed =
        new HashSet<string>(System.StringComparer.OrdinalIgnoreCase) { "Click", "Type", "PressKey", "Log" };

    private static PerceivedScreen Screen(IReadOnlyList<string> elements, IReadOnlyList<string>? signatures = null) =>
        new("hash", Scrubbed: true, ElementSummary: elements, WindowTitle: "Calculator", Signatures: signatures);

    [Fact]
    public void EnumeratesClickByLabel_FromRoleNameLines_SkipsTextLines()
    {
        var screen = Screen(new[] { "button:Seven", "button:Plus", "text:Display 0", "menuitem:View" });
        var cands = CandidateEnumerator.Enumerate(screen, ClickAllowed, "calc");

        var labels = cands.Where(c => c.Verb == "click_by_label")
            .Select(c => c.Parameters["label"]).ToList();
        Assert.Contains("Seven", labels);
        Assert.Contains("Plus", labels);
        Assert.Contains("View", labels);
        Assert.DoesNotContain("Display 0", labels); // text: lines are not clickable targets
        Assert.All(cands.Where(c => c.Verb == "click_by_label"),
            c => Assert.Equal("calc", c.Parameters["process_name"]));
    }

    [Fact]
    public void EnumeratesClickBySignature_FromSignatures()
    {
        var screen = Screen(new[] { "button:Seven" }, signatures: new[] { "Button|num7Button", "Button|plusButton" });
        var cands = CandidateEnumerator.Enumerate(screen, ClickAllowed, "calc");

        var sig = cands.FirstOrDefault(c => c.Verb == "click_by_signature" && (string?)c.Parameters["automationId"] == "num7Button");
        Assert.NotEqual(default, sig);
        Assert.Equal("Button", sig.Parameters["controlType"]);
        Assert.Equal("calc", sig.Parameters["process_name"]);
    }

    [Fact]
    public void ActionSig_EqualsEmittedNextActionSignature()
    {
        var screen = Screen(new[] { "button:Save" });
        var cand = CandidateEnumerator.Enumerate(screen, ClickAllowed, "notepad").Single();
        var emitted = NextAction.Act(cand.Verb, cand.Parameters).Signature();
        Assert.Equal(emitted, cand.ActionSig);
    }

    [Fact]
    public void NullScreen_OrEmptyProcess_OrNoClickVerb_YieldsEmpty()
    {
        Assert.Empty(CandidateEnumerator.Enumerate(null, ClickAllowed, "calc"));
        Assert.Empty(CandidateEnumerator.Enumerate(Screen(new[] { "button:Seven" }), ClickAllowed, ""));
        var noClick = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase) { "Log", "WaitForElement" };
        Assert.Empty(CandidateEnumerator.Enumerate(Screen(new[] { "button:Seven" }), noClick, "calc"));
    }

    [Fact]
    public void Deduplicates_IdenticalCandidates()
    {
        var screen = Screen(new[] { "button:Seven", "button:Seven" });
        Assert.Single(CandidateEnumerator.Enumerate(screen, ClickAllowed, "calc"));
    }

    [Fact]
    public void NullSignatures_AreSafe()
    {
        var screen = Screen(new[] { "button:Seven" }, signatures: null);
        Assert.Single(CandidateEnumerator.Enumerate(screen, ClickAllowed, "calc"));
    }

    [Fact]
    public void CapsToMaxCandidates()
    {
        var many = Enumerable.Range(0, 200).Select(i => $"button:Key{i}").ToList();
        var cands = CandidateEnumerator.Enumerate(Screen(many), ClickAllowed, "calc", maxCandidates: 50);
        Assert.Equal(50, cands.Count);
    }
}
