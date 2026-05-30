using System;
using System.Collections.Generic;
using SuavoAgent.Contracts.Adapters;
using SuavoAgent.Core.Adapters;
using Xunit;

namespace SuavoAgent.Core.Tests.Adapters;

/// <summary>
/// The registry is the typed home for per-adapter Core config + the enforced
/// HIPAA invariant (#1): no adapter may register without a non-empty PHI policy.
/// Lookups normalize the AdapterType key so "PioneerRx" / " pioneerrx " resolve
/// the same registered config.
/// </summary>
public class AdapterRegistryTests
{
    private static PhiPolicy ValidPhi() =>
        PhiPolicy.Create(
            new HashSet<string>(new[] { "PatientName" }, StringComparer.OrdinalIgnoreCase),
            new[] { "patient" });

    private static AdapterConfig Config(string adapterType, PhiPolicy? phi = null) => new()
    {
        AdapterType = adapterType,
        DisplayName = adapterType,
        Process = new ProcessIdentity(adapterType, "Main"),
        Phi = phi ?? ValidPhi(),
        Sql = new SqlProfile($"{adapterType}Db", $"{adapterType}.sql.v1"),
    };

    [Fact]
    public void Resolve_RegisteredType_ReturnsConfig()
    {
        var reg = new AdapterRegistry(new[] { Config("pioneerrx") }, "pioneerrx");
        Assert.Equal("pioneerrx", reg.Resolve("pioneerrx").AdapterType);
    }

    [Fact]
    public void Resolve_NormalizesCaseAndWhitespace()
    {
        var reg = new AdapterRegistry(new[] { Config("pioneerrx") }, "pioneerrx");
        Assert.Equal("pioneerrx", reg.Resolve("  PioneerRx  ").AdapterType);
    }

    [Fact]
    public void Resolve_UnknownType_Throws()
    {
        var reg = new AdapterRegistry(new[] { Config("pioneerrx") }, "pioneerrx");
        Assert.Throws<AdapterNotRegisteredException>(() => reg.Resolve("computerrx"));
    }

    [Fact]
    public void TryResolve_Unknown_ReturnsFalse()
    {
        var reg = new AdapterRegistry(new[] { Config("pioneerrx") }, "pioneerrx");
        Assert.False(reg.TryResolve("nope", out _));
    }

    [Fact]
    public void TryResolve_Known_ReturnsTrueAndConfig()
    {
        var reg = new AdapterRegistry(new[] { Config("pioneerrx") }, "pioneerrx");
        Assert.True(reg.TryResolve("PIONEERRX", out var cfg));
        Assert.Equal("pioneerrx", cfg!.AdapterType);
    }

    [Fact]
    public void Default_ReturnsDefaultTypedConfig()
    {
        var reg = new AdapterRegistry(new[] { Config("pioneerrx"), Config("computerrx") }, "pioneerrx");
        Assert.Equal("pioneerrx", reg.Default.AdapterType);
    }

    [Fact]
    public void All_ReturnsEveryConfig()
    {
        var reg = new AdapterRegistry(new[] { Config("pioneerrx"), Config("computerrx") }, "pioneerrx");
        Assert.Equal(2, reg.All.Count);
    }

    [Fact]
    public void Ctor_DuplicateNormalizedKeys_Throws()
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            new AdapterRegistry(new[] { Config("pioneerrx"), Config("PioneerRx") }, "pioneerrx"));
    }

    [Fact]
    public void Ctor_ConfigWithEmptyPhiPolicy_Throws()
    {
        var emptyPhi = new PhiPolicy
        {
            ColumnBlocklist = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            ColumnPatterns = Array.Empty<string>(),
        };
        Assert.ThrowsAny<ArgumentException>(() =>
            new AdapterRegistry(new[] { Config("pioneerrx", emptyPhi) }, "pioneerrx"));
    }

    [Fact]
    public void Ctor_DefaultTypeNotRegistered_Throws()
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            new AdapterRegistry(new[] { Config("pioneerrx") }, "computerrx"));
    }
}
