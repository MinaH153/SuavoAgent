using System;
using System.Collections.Generic;
using SuavoAgent.Contracts.Ipc;
using Xunit;

namespace SuavoAgent.Contracts.Tests.Ipc;

/// <summary>
/// The actuation app-allowlist is operator-extensible (canary/non-PHI boxes add apps via
/// actuation.json's AllowedApps) but the built-in Notepad/Calculator defaults are always retained
/// and never overridable — and malformed/path/wildcard entries are rejected so config can't smuggle
/// an arbitrary executable. Resets to defaults after each test (shared static registry).
/// </summary>
public sealed class ActuationAllowlistTests : IDisposable
{
    public void Dispose() => ActuationAllowlistedSandboxApps.ExtendAllowlist(null);

    [Fact]
    public void Defaults_AlwaysPresent_EvenAfterNullExtend()
    {
        ActuationAllowlistedSandboxApps.ExtendAllowlist(null);
        var apps = ActuationAllowlistedSandboxApps.ProcessNames;
        Assert.Equal("notepad.exe", apps["notepad"]);
        Assert.Equal("calc.exe", apps["calculator"]);
    }

    [Fact]
    public void ExtendAllowlist_AddsOperatorAuthorizedApps_KeepingDefaults()
    {
        ActuationAllowlistedSandboxApps.ExtendAllowlist(new Dictionary<string, string>
        {
            ["mspaint"] = "mspaint.exe",
            ["explorer"] = "explorer.exe",
        });
        var apps = ActuationAllowlistedSandboxApps.ProcessNames;
        Assert.Equal("mspaint.exe", apps["mspaint"]);
        Assert.Equal("explorer.exe", apps["explorer"]);
        Assert.True(apps.ContainsKey("notepad"));     // defaults retained
        Assert.True(apps.ContainsKey("calculator"));
    }

    [Fact]
    public void ExtendAllowlist_RejectsPaths_Wildcards_AndNonExe()
    {
        ActuationAllowlistedSandboxApps.ExtendAllowlist(new Dictionary<string, string>
        {
            ["evilpath"] = @"C:\Windows\System32\cmd.exe", // path separators + colon → rejected
            ["wild"] = "*.exe",                            // wildcard → rejected
            ["noext"] = "powershell",                      // not .exe → rejected
            ["script"] = "run.bat",                        // not .exe → rejected
            ["blank"] = "   ",                             // empty → rejected
        });
        var apps = ActuationAllowlistedSandboxApps.ProcessNames;
        Assert.False(apps.ContainsKey("evilpath"));
        Assert.False(apps.ContainsKey("wild"));
        Assert.False(apps.ContainsKey("noext"));
        Assert.False(apps.ContainsKey("script"));
        Assert.False(apps.ContainsKey("blank"));
    }

    [Fact]
    public void ExtendAllowlist_CannotOverrideOrRepointADefault()
    {
        ActuationAllowlistedSandboxApps.ExtendAllowlist(new Dictionary<string, string>
        {
            ["notepad"] = "evil.exe", // attempt to repoint the notepad default → ignored
        });
        Assert.Equal("notepad.exe", ActuationAllowlistedSandboxApps.ProcessNames["notepad"]);
    }
}
