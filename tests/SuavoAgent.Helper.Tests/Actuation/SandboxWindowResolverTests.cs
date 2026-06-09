using SuavoAgent.Helper.Actuation;
using Xunit;

namespace SuavoAgent.Helper.Tests.Actuation;

/// <summary>
/// Pins the platform-independent UWP-alias allowlist matcher: the effective process behind a sandbox
/// window (e.g. Win11 Calculator's real process "CalculatorApp", NOT the "ApplicationFrameHost" frame) is
/// recognised as allowlisted, while the AFH frame host and the PMS are NOT. This is what lets UWP apps be
/// captured without ever allowlisting ApplicationFrameHost. The Win32 drilling itself is on-box (PARTIAL).
/// </summary>
public class SandboxWindowResolverTests
{
    [Theory]
    [InlineData("notepad")]      // built-in default (key + launcher base name)
    [InlineData("calculator")]   // built-in default key
    [InlineData("calc")]         // calc.exe launcher base name
    [InlineData("Calculator")]   // packaged alias of calc
    [InlineData("CalculatorApp")]// real Win11 UWP process behind the AFH frame
    public void IsAllowlistedSandboxProcess_AllowsSandboxAppsAndUwpAliases(string name)
    {
        Assert.True(SandboxWindowResolver.IsAllowlistedSandboxProcess(name));
    }

    [Theory]
    [InlineData("ApplicationFrameHost")] // the UWP FRAME host — must NOT be allowlisted (the whole reason we drill)
    [InlineData("PioneerPharmacy")]      // the PMS — never allowlisted
    [InlineData("svchost")]              // arbitrary system process — never a sandbox app
    [InlineData("")]
    [InlineData(null)]
    public void IsAllowlistedSandboxProcess_RefusesFrameHostPmsAndUnknown(string? name)
    {
        Assert.False(SandboxWindowResolver.IsAllowlistedSandboxProcess(name));
    }

    [Fact]
    public void EffectiveAppPid_ZeroHwnd_ReturnsZero()
    {
        Assert.Equal(0, SandboxWindowResolver.EffectiveAppPid(IntPtr.Zero));
    }

    [Fact]
    public void IsSandboxAppForeground_NonPositivePid_ReturnsFalse()
    {
        Assert.False(SandboxWindowResolver.IsSandboxAppForeground(0));
        Assert.False(SandboxWindowResolver.IsSandboxAppForeground(-1));
    }
}
