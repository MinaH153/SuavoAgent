using System.Text.Json;
using SuavoAgent.Contracts.Learning;
using Xunit;

namespace SuavoAgent.Contracts.Tests.Learning;

/// <summary>
/// KeyHint is the closed, non-PHI replacement for the originally-proposed
/// open <c>IReadOnlyDictionary&lt;string,string&gt;</c> hint bag. Every field is
/// a whitelisted UIA key name or a KeyHintPlaceholder enum value — there is
/// no path for operator-entered text or UI literals to ride through.
/// </summary>
public class KeyHintTests
{
    [Fact]
    public void PlaceholderEnum_EnumeratesOnlyApprovedTokens()
    {
        var names = System.Enum.GetNames<KeyHintPlaceholder>();
        // Any addition here must be reviewed for PHI implications.
        Assert.Equal(
            new[] { "RxNumberEchoed", "CurrentDateIsoUtc", "AgentUserNameFromAdapter" },
            names);
    }

    [Fact]
    public void KeyName_Only()
    {
        var hint = new KeyHint(KeyName: "Enter", Placeholder: null);
        Assert.Equal("Enter", hint.KeyName);
        Assert.Null(hint.Placeholder);
    }

    [Fact]
    public void Placeholder_Only()
    {
        var hint = new KeyHint(KeyName: null, Placeholder: KeyHintPlaceholder.RxNumberEchoed);
        Assert.Null(hint.KeyName);
        Assert.Equal(KeyHintPlaceholder.RxNumberEchoed, hint.Placeholder);
    }

    [Fact]
    public void CanonicalRepr_KeyNameOnly()
    {
        var hint = new KeyHint(KeyName: "Enter", Placeholder: null);
        Assert.Equal("Enter|", hint.CanonicalRepr);
    }

    [Fact]
    public void CanonicalRepr_PlaceholderOnly()
    {
        var hint = new KeyHint(KeyName: null, Placeholder: KeyHintPlaceholder.CurrentDateIsoUtc);
        Assert.Equal("|CurrentDateIsoUtc", hint.CanonicalRepr);
    }

    [Fact]
    public void CanonicalRepr_Both()
    {
        var hint = new KeyHint(KeyName: "Tab", Placeholder: KeyHintPlaceholder.AgentUserNameFromAdapter);
        Assert.Equal("Tab|AgentUserNameFromAdapter", hint.CanonicalRepr);
    }

    [Fact]
    public void CanonicalRepr_BothNull()
    {
        var hint = new KeyHint(KeyName: null, Placeholder: null);
        Assert.Equal("|", hint.CanonicalRepr);
    }

    [Fact]
    public void JsonRoundTrip()
    {
        var hint = new KeyHint("Enter", KeyHintPlaceholder.RxNumberEchoed);
        var json = JsonSerializer.Serialize(hint);
        var parsed = JsonSerializer.Deserialize<KeyHint>(json);
        Assert.NotNull(parsed);
        Assert.Equal(hint, parsed);
    }

    [Fact]
    public void Contract_HasNoOpenDictionaryField()
    {
        // Reflection guard against regression: KeyHint must remain a closed
        // record — no IDictionary, no IReadOnlyDictionary anywhere on its surface.
        var type = typeof(KeyHint);
        foreach (var prop in type.GetProperties())
        {
            var n = prop.PropertyType.Name;
            Assert.DoesNotContain("Dictionary", n);
            Assert.DoesNotContain("IDictionary", n);
        }
    }
}
