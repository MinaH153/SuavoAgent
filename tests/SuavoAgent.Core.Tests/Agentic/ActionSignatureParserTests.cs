using System.Collections.Generic;
using SuavoAgent.Core.Agentic;
using Xunit;

namespace SuavoAgent.Core.Tests.Agentic;

/// <summary>
/// Pins the signature ↔ action round-trip the replayer relies on: a clean click signature parses back to
/// the same action; terminal/malformed signatures return null; and a value containing a delimiter does NOT
/// round-trip (so the caller's equality check rejects it rather than executing a misparse).
/// </summary>
public class ActionSignatureParserTests
{
    private static string Sig(params (string k, string v)[] kv)
    {
        var p = new Dictionary<string, object?>();
        foreach (var (k, v) in kv) p[k] = v;
        return NextAction.Act("click_by_label", p).Signature();
    }

    [Fact]
    public void CleanClickSignature_RoundTrips()
    {
        var sig = Sig(("label", "Seven"), ("process_name", "calc.exe"));
        var parsed = ActionSignatureParser.TryParse(sig);
        Assert.NotNull(parsed);
        Assert.Equal(NextActionKind.Act, parsed!.Kind);
        Assert.Equal("click_by_label", parsed.Verb);
        Assert.Equal(sig, parsed.Signature()); // the load-bearing equality the replayer checks
    }

    [Fact]
    public void EmptyParams_RoundTrips()
    {
        var sig = NextAction.Act("launch_sandbox_app").Signature(); // "launch_sandbox_app()"
        var parsed = ActionSignatureParser.TryParse(sig);
        Assert.NotNull(parsed);
        Assert.Equal(sig, parsed!.Signature());
    }

    [Theory]
    [InlineData("Done")]
    [InlineData("Escalate")]
    [InlineData("")]
    [InlineData("no_parens")]
    [InlineData("(missingverb=1)")]
    public void TerminalOrMalformed_ReturnsNull(string sig)
    {
        Assert.Null(ActionSignatureParser.TryParse(sig));
    }

    [Fact]
    public void ValueWithDelimiter_DoesNotRoundTrip_SoCallerRejects()
    {
        // A label literally containing a comma can't round-trip through the unescaped verb(k=v,...) form.
        var sig = Sig(("label", "A, B"), ("process_name", "calc.exe"));
        var parsed = ActionSignatureParser.TryParse(sig);
        // Either it fails to parse, or it parses to something whose re-signature differs — both make the
        // replayer's `parsed.Signature() == sig` guard reject it. It must NEVER silently reconstruct "A, B".
        Assert.True(parsed is null || parsed.Signature() != sig);
    }
}
