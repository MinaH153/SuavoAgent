using SuavoAgent.Verbs;
using SuavoAgent.Verbs.Verbs;
using Xunit;

namespace SuavoAgent.Verbs.Tests;

public class VerbRegistryTests
{
    [Fact]
    public void Registry_ResolvesKnownVerb()
    {
        var reg = new VerbRegistry(new IVerb[] { new RestartServiceVerb() });
        var v = reg.Resolve("restart_service", "1.0.0");
        Assert.NotNull(v);
        Assert.Equal("restart_service", v.Metadata.Name);
    }

    [Fact]
    public void Registry_ReturnsNull_ForUnknownVerb()
    {
        var reg = new VerbRegistry(new IVerb[] { new RestartServiceVerb() });
        Assert.Null(reg.Resolve("nonexistent", "1.0.0"));
    }

    [Fact]
    public void Registry_ReturnsNull_ForWrongVersion()
    {
        var reg = new VerbRegistry(new IVerb[] { new RestartServiceVerb() });
        Assert.Null(reg.Resolve("restart_service", "2.0.0"));
    }

    [Fact]
    public void Registry_RejectsDuplicate()
    {
        var ex = Record.Exception(() =>
            new VerbRegistry(new IVerb[] { new RestartServiceVerb(), new RestartServiceVerb() }));
        Assert.IsType<InvalidOperationException>(ex);
        Assert.Contains("duplicate", ex.Message);
    }

    [Fact]
    public void SchemaHash_IsDeterministic()
    {
        var r1 = new VerbRegistry(new IVerb[] { new RestartServiceVerb() });
        var r2 = new VerbRegistry(new IVerb[] { new RestartServiceVerb() });
        Assert.Equal(r1.SchemaHash("restart_service", "1.0.0"), r2.SchemaHash("restart_service", "1.0.0"));
    }

    [Fact]
    public void SchemaHash_DiffersAcrossVerbs()
    {
        var reg = new VerbRegistry(new IVerb[]
        {
            new RestartServiceVerb(),
            new InvokeSchemaCanaryVerb()
        });
        var h1 = reg.SchemaHash("restart_service", "1.0.0");
        var h2 = reg.SchemaHash("invoke_schema_canary", "1.0.0");
        Assert.NotEqual(h1, h2);
    }

    [Fact]
    public void SchemaHash_Is64HexChars()
    {
        var reg = new VerbRegistry(new IVerb[] { new RestartServiceVerb() });
        var hash = reg.SchemaHash("restart_service", "1.0.0");
        Assert.Matches("^[0-9a-f]{64}$", hash);
    }
}
