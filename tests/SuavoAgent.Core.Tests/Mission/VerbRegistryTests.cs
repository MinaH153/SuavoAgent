using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Core.ActionGrammarV1;
using SuavoAgent.Core.ActionGrammarV1.Verbs.Actuation;
using SuavoAgent.Core.ActionGrammarV1.Verbs.LookupPatient;
using Xunit;

namespace SuavoAgent.Core.Tests.Mission;

public sealed class VerbRegistryTests
{
    [Fact]
    public void Build_DiscoversAllIVerbImplementations()
    {
        var registry = VerbRegistry.Build(
            new[] { typeof(IVerb).Assembly },
            NullLogger<VerbRegistry>.Instance);

        Assert.Contains(registry.All, e => e.Name == LookupPatientVerb.VerbName);
        Assert.Contains(registry.All, e => e.Name == ClickByLabelVerb.VerbName);
        Assert.Contains(registry.All, e => e.Name == TypeIntoFieldVerb.VerbName);
        Assert.Contains(registry.All, e => e.Name == PressKeysVerb.VerbName);
        Assert.Contains(registry.All, e => e.Name == LaunchSandboxAppVerb.VerbName);
    }

    [Fact]
    public void ManifestHash_IsDeterministic_AcrossInstances()
    {
        var verb = new ClickByLabelVerb();
        var hash1 = VerbRegistry.ComputeManifestHash(verb.Metadata);
        var hash2 = VerbRegistry.ComputeManifestHash(verb.Metadata);
        Assert.Equal(hash1, hash2);
        Assert.Equal(64, hash1.Length); // SHA-256 hex
    }

    [Fact]
    public void ManifestHash_DiffersAcrossDifferentVerbs()
    {
        var clickHash = VerbRegistry.ComputeManifestHash(new ClickByLabelVerb().Metadata);
        var typeHash = VerbRegistry.ComputeManifestHash(new TypeIntoFieldVerb().Metadata);
        Assert.NotEqual(clickHash, typeHash);
    }

    [Fact]
    public void Resolve_NullExpectedHash_ReturnsEntry()
    {
        var registry = VerbRegistry.Build(
            new[] { typeof(IVerb).Assembly },
            NullLogger<VerbRegistry>.Instance);

        var entry = registry.Resolve(ClickByLabelVerb.VerbName, expectedManifestHash: null, out var reason);
        Assert.NotNull(entry);
        Assert.Null(reason);
    }

    [Fact]
    public void Resolve_MismatchedHash_ReturnsNullWithReason()
    {
        var registry = VerbRegistry.Build(
            new[] { typeof(IVerb).Assembly },
            NullLogger<VerbRegistry>.Instance);

        var entry = registry.Resolve(ClickByLabelVerb.VerbName, expectedManifestHash: "deadbeef", out var reason);
        Assert.Null(entry);
        Assert.NotNull(reason);
        Assert.Contains("manifest_hash_mismatch", reason);
    }

    [Fact]
    public void Resolve_UnknownName_ReturnsNullWithReason()
    {
        var registry = VerbRegistry.Build(
            new[] { typeof(IVerb).Assembly },
            NullLogger<VerbRegistry>.Instance);

        var entry = registry.Resolve("not_a_verb", expectedManifestHash: null, out var reason);
        Assert.Null(entry);
        Assert.NotNull(reason);
        Assert.Contains("not registered", reason);
    }
}
