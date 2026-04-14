using SuavoAgent.Core.Config;
using Xunit;

namespace SuavoAgent.Core.Tests.Config;

public class IndustryAdapterTests
{
    [Fact]
    public void ClassifyDomain_ReturnsCorrectCategory()
    {
        var adapter = new IndustryAdapter
        {
            KnownDomains = new()
            {
                ["insurance"] = new() { "express-scripts.com", "optumrx.com" },
                ["regulatory"] = new() { "fda.gov" }
            }
        };
        Assert.Equal("insurance", adapter.ClassifyDomain("express-scripts.com"));
        Assert.Equal("regulatory", adapter.ClassifyDomain("fda.gov"));
        Assert.Null(adapter.ClassifyDomain("google.com"));
    }

    [Fact]
    public void ClassifyDomain_CaseInsensitive()
    {
        var adapter = new IndustryAdapter
        {
            KnownDomains = new() { ["insurance"] = new() { "OptumRx.com" } }
        };
        Assert.Equal("insurance", adapter.ClassifyDomain("optumrx.com"));
    }

    [Fact]
    public void IsPrimaryApp_MatchesCaseInsensitive()
    {
        var adapter = new IndustryAdapter { PrimaryApps = new() { "PioneerPharmacy.exe" } };
        Assert.True(adapter.IsPrimaryApp("PioneerPharmacy.exe"));
        Assert.True(adapter.IsPrimaryApp("pioneerpharMacy.exe"));
        Assert.False(adapter.IsPrimaryApp("chrome.exe"));
    }

    [Fact]
    public void LoadForIndustry_ReturnsFallback_WhenNoFile()
    {
        var adapter = IndustryAdapter.LoadForIndustry("nonexistent", Path.GetTempPath());
        Assert.Equal("nonexistent", adapter.Industry);
        Assert.Empty(adapter.PrimaryApps);
    }
}
