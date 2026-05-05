using SuavoAgent.Helper.Actuation;
using Xunit;

namespace SuavoAgent.Helper.Tests.Actuation;

public sealed class KeyChordTests
{
    [Fact]
    public void Parse_SingleKey_NoModifiers()
    {
        Assert.True(KeyChord.TryParse("Enter", out var chord));
        Assert.NotNull(chord);
        Assert.Empty(chord!.Modifiers);
        Assert.Equal(VirtualKey.Enter, chord.MainKey);
    }

    [Fact]
    public void Parse_CtrlS()
    {
        Assert.True(KeyChord.TryParse("Ctrl+S", out var chord));
        Assert.NotNull(chord);
        Assert.Single(chord!.Modifiers, VirtualKey.Control);
        Assert.Equal(VirtualKey.S, chord.MainKey);
    }

    [Fact]
    public void Parse_CtrlShiftEsc()
    {
        Assert.True(KeyChord.TryParse("Ctrl+Shift+Esc", out var chord));
        Assert.NotNull(chord);
        Assert.Equal(2, chord!.Modifiers.Count);
        Assert.Contains(VirtualKey.Control, chord.Modifiers);
        Assert.Contains(VirtualKey.Shift, chord.Modifiers);
        Assert.Equal(VirtualKey.Escape, chord.MainKey);
    }

    [Theory]
    [InlineData("F1", VirtualKey.F1)]
    [InlineData("f12", VirtualKey.F12)]
    [InlineData("F8", VirtualKey.F8)]
    public void Parse_FunctionKeys(string raw, VirtualKey expected)
    {
        Assert.True(KeyChord.TryParse(raw, out var chord));
        Assert.Equal(expected, chord!.MainKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData("    ")]
    [InlineData("Ctrl+")]
    [InlineData("not_a_key")]
    [InlineData("Ctrl+totallyinvalid")]
    [InlineData("Ctrl+Shift")] // ends on a modifier
    public void Parse_InvalidInputs_Fail(string raw)
    {
        Assert.False(KeyChord.TryParse(raw, out var chord));
        Assert.Null(chord);
    }

    [Fact]
    public void Parse_AllowsAliases()
    {
        Assert.True(KeyChord.TryParse("Control+A", out var chord));
        Assert.Equal(VirtualKey.A, chord!.MainKey);
        Assert.Single(chord.Modifiers, VirtualKey.Control);
    }

    [Fact]
    public void Parse_Whitespace_Tolerated()
    {
        Assert.True(KeyChord.TryParse("Ctrl + Shift + Esc", out var chord));
        Assert.Equal(VirtualKey.Escape, chord!.MainKey);
    }
}
