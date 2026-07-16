using System;
using System.Collections.Generic;
using SuavoAgent.Contracts.Ipc;
using Xunit;

namespace SuavoAgent.Contracts.Tests.Ipc;

/// <summary>
/// The sandbox policy is immutable at runtime. Calculator is the only default; Notepad is protected
/// because modern Windows can route launches into an existing tabbed process containing PHI.
/// </summary>
public sealed class ActuationAllowlistTests : IDisposable
{
    public void Dispose() => ActuationAllowlistedSandboxApps.ExtendAllowlist(null);

    [Fact]
    public void Defaults_AlwaysPresent_EvenAfterNullExtend()
    {
        ActuationAllowlistedSandboxApps.ExtendAllowlist(null);
        var apps = ActuationAllowlistedSandboxApps.ProcessNames;
        Assert.Equal("calc.exe", apps["calculator"]);
        Assert.False(apps.ContainsKey("notepad"));
    }

    [Fact]
    public void ExtendAllowlist_IgnoresAllRuntimeAdditions()
    {
        ActuationAllowlistedSandboxApps.ExtendAllowlist(new Dictionary<string, string>
        {
            ["mspaint"] = "mspaint.exe",
        });
        var apps = ActuationAllowlistedSandboxApps.ProcessNames;
        Assert.False(apps.ContainsKey("mspaint"));
        Assert.False(apps.ContainsKey("notepad"));
        Assert.True(apps.ContainsKey("calculator"));
    }

    [Theory]
    [InlineData("pioneer", "PioneerPharmacy.exe")]
    [InlineData("PIONEERRX", "renamed.exe")]
    [InlineData("browser", "chrome.exe")]
    [InlineData("office", "EXCEL.EXE")]
    [InlineData("shell", "powershell.exe")]
    public void ExtendAllowlist_ImmutableProtectedProcessesCannotBeAdded(string key, string process)
    {
        ActuationAllowlistedSandboxApps.ExtendAllowlist(new Dictionary<string, string>
        {
            [key] = process,
        });

        Assert.False(ActuationAllowlistedSandboxApps.ProcessNames.ContainsKey(key));
        Assert.False(ActuationAllowlistedSandboxApps.IsDeclaredSandboxProcess(process));
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
            ["calculator"] = "evil.exe",
        });
        Assert.Equal("calc.exe", ActuationAllowlistedSandboxApps.ProcessNames["calculator"]);
    }
}
