using System.ComponentModel;
using SuavoAgent.Helper.Actuation;
using Xunit;

namespace SuavoAgent.Helper.Tests.Actuation;

public sealed class SendInputValidatorTests
{
    [Fact]
    public void EnsureFullyInjected_AllSent_DoesNotThrow()
    {
        var ex = Record.Exception(() =>
            SendInputValidator.EnsureFullyInjected("type_text_char", sent: 2, expected: 2, win32Error: 0));
        Assert.Null(ex);
    }

    [Fact]
    public void EnsureFullyInjected_ZeroSent_ThrowsWin32WithNativeErrorCode()
    {
        // Win32 error 5 = ERROR_ACCESS_DENIED, the signature failure for Bug 22
        // (Helper running with an Identification-level token instead of
        // Impersonation). Surfacing the code lets us match new occurrences
        // back to the Bug 22 fix in MinaH153/SuavoAgent#65.
        var ex = Assert.Throws<Win32Exception>(() =>
            SendInputValidator.EnsureFullyInjected("type_text_char", sent: 0, expected: 2, win32Error: 5));
        Assert.Equal(5, ex.NativeErrorCode);
    }

    [Fact]
    public void EnsureFullyInjected_PartialSent_ThrowsWin32()
    {
        var ex = Assert.Throws<Win32Exception>(() =>
            SendInputValidator.EnsureFullyInjected("send_chord", sent: 1, expected: 4, win32Error: 0));
        Assert.Contains("send_chord", ex.Message);
        Assert.Contains("1 of 4", ex.Message);
    }

    [Fact]
    public void EnsureFullyInjected_Message_IncludesVerbCountsAndWin32Code()
    {
        var ex = Assert.Throws<Win32Exception>(() =>
            SendInputValidator.EnsureFullyInjected("move_and_click", sent: 0, expected: 3, win32Error: 5));
        Assert.Contains("move_and_click", ex.Message);
        Assert.Contains("0 of 3", ex.Message);
        Assert.Contains("0x00000005", ex.Message);
    }

    [Fact]
    public void EnsureFullyInjected_OverInjection_AlsoThrows()
    {
        // SendInput should never return more than nInputs per spec, but if it
        // ever did we want a loud signal rather than silent acceptance.
        var ex = Assert.Throws<Win32Exception>(() =>
            SendInputValidator.EnsureFullyInjected("type_text_char", sent: 5, expected: 2, win32Error: 0));
        Assert.Contains("5 of 2", ex.Message);
    }

    [Fact]
    public void EnsureFullyInjected_NegativeExpected_ThrowsArgumentOutOfRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SendInputValidator.EnsureFullyInjected("send_chord", sent: 0, expected: -1, win32Error: 0));
    }
}
