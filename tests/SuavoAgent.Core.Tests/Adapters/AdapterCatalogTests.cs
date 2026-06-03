using System;
using System.Collections.Generic;
using SuavoAgent.Contracts.Adapters;
using SuavoAgent.Core.Adapters;
using Xunit;

namespace SuavoAgent.Core.Tests.Adapters;

/// <summary>
/// Catalog resolution after adapter selection — the fix for the Codex Q3 trap, where a
/// hardcoded "PioneerPharmacySystem" default would shadow a non-PioneerRx adapter's catalog.
/// A pharmacy's explicit SqlDatabase wins; otherwise we fall back to the resolved adapter's
/// own default catalog (never PioneerRx's, for a non-PioneerRx adapter).
/// </summary>
public class AdapterCatalogTests
{
    private static AdapterConfig Cfg(string type, string catalog) => new()
    {
        AdapterType = type,
        DisplayName = type,
        Process = new ProcessIdentity(type, "Main"),
        Phi = PhiPolicy.Create(
            new HashSet<string>(new[] { "PatientName" }, StringComparer.OrdinalIgnoreCase),
            new[] { "patient" }),
        Sql = new SqlProfile(catalog, $"{type}.v1"),
    };

    [Fact]
    public void Resolve_NullSqlDatabase_UsesConfigDefaultCatalog()
    {
        Assert.Equal("PioneerPharmacySystem",
            AdapterCatalog.Resolve(null, Cfg("pioneerrx", "PioneerPharmacySystem")));
    }

    [Fact]
    public void Resolve_WhitespaceSqlDatabase_UsesConfigDefaultCatalog()
    {
        Assert.Equal("PioneerPharmacySystem",
            AdapterCatalog.Resolve("   ", Cfg("pioneerrx", "PioneerPharmacySystem")));
    }

    [Fact]
    public void Resolve_NonPioneerRxAdapter_GetsItsOwnCatalog_NotPioneerRx()
    {
        Assert.Equal("ComputerRxDb",
            AdapterCatalog.Resolve(null, Cfg("computerrx", "ComputerRxDb")));
    }

    [Fact]
    public void Resolve_ExplicitSqlDatabase_Wins()
    {
        Assert.Equal("CustomDb",
            AdapterCatalog.Resolve("CustomDb", Cfg("pioneerrx", "PioneerPharmacySystem")));
    }
}
