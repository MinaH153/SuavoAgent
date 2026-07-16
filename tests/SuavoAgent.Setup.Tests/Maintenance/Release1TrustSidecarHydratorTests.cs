using System.Net;
using System.Text;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Setup.Maintenance;
using Xunit;

namespace SuavoAgent.Setup.Tests.Maintenance;

public sealed class Release1TrustSidecarHydratorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "suavo-release1-sidecars-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Hmac_authenticated_exact_assets_publish_only_after_staged_validation()
    {
        var install = Path.Combine(_root, "Program Files", "Suavo", "Agent");
        var data = Path.Combine(_root, "ProgramData", "SuavoAgent");
        var proof = Path.Combine(_root, "ProgramData", "SuavoAgent-InstallerProof");
        Directory.CreateDirectory(install);
        Directory.CreateDirectory(data);
        Release1MsiInstallMarkerStore.CreateAndProtectProofDirectory(proof);
        var expectedAssets = Assets();
        var handler = new SidecarHandler(expectedAssets);

        var result = await Release1TrustSidecarHydrator.HydrateAsync(
            Config(),
            install,
            data,
            CancellationToken.None,
            handler,
            (directory, _) => expectedAssets.Keys.All(asset =>
                    File.Exists(Path.Combine(directory, asset)))
                ? SignedReleaseCohortValidation.Valid(Evidence())
                : SignedReleaseCohortValidation.Reject("not_yet_hydrated"),
            (_, receipts, _) => expectedAssets.Keys.All(asset =>
                    File.Exists(Path.Combine(receipts, asset)))
                ? SignedReleaseCohortValidation.Valid(Evidence())
                : SignedReleaseCohortValidation.Reject("staged_incomplete"),
            protectedStageRoot: proof);

        Assert.True(result.Succeeded, result.Code);
        Assert.Equal("hydrated", result.Code);
        Assert.Equal(expectedAssets.Keys.Order(), handler.RequestedAssets.Order());
        Assert.All(expectedAssets, pair => Assert.Equal(
            pair.Value,
            File.ReadAllBytes(Path.Combine(install, pair.Key))));
        Assert.All(handler.Authorizations, authorization =>
        {
            Assert.Equal("2", authorization.Version);
            Assert.Matches("^[0-9]{13}$", authorization.Timestamp);
            Assert.Matches("^[A-Za-z0-9_-]{43}$", authorization.Nonce);
            Assert.Matches("^[a-f0-9]{64}$", authorization.BodySha256);
            Assert.Matches("^[a-f0-9]{64}$", authorization.Signature);
        });
        Assert.Empty(Directory.EnumerateDirectories(data));
        Assert.Empty(Directory.EnumerateDirectories(proof));
    }

    [Fact]
    public async Task Redirect_is_rejected_without_publishing_any_sidecar()
    {
        var install = Path.Combine(_root, "Program Files", "Suavo", "Agent");
        var data = Path.Combine(_root, "ProgramData", "SuavoAgent");
        var proof = Path.Combine(_root, "ProgramData", "SuavoAgent-InstallerProof");
        Directory.CreateDirectory(install);
        Directory.CreateDirectory(data);
        Release1MsiInstallMarkerStore.CreateAndProtectProofDirectory(proof);
        var handler = new RedirectHandler();

        var result = await Release1TrustSidecarHydrator.HydrateAsync(
            Config(),
            install,
            data,
            CancellationToken.None,
            handler,
            (_, _) => SignedReleaseCohortValidation.Reject("missing"),
            (_, _, _) => throw new InvalidOperationException(
                "redirected bytes must never reach validation"),
            protectedStageRoot: proof);

        Assert.False(result.Succeeded);
        Assert.Equal("sidecar_redirect_rejected", result.Code);
        Assert.Empty(Directory.EnumerateFiles(install));
    }

    [Fact]
    public async Task Junction_proof_root_cannot_turn_setup_into_fixed_name_privileged_writer()
    {
        var install = Path.Combine(_root, "Program Files", "Suavo", "Agent");
        var data = Path.Combine(_root, "ProgramData", "SuavoAgent");
        var victim = Path.Combine(_root, "privileged-target");
        var proofLink = Path.Combine(_root, "ProgramData", "SuavoAgent-InstallerProof");
        Directory.CreateDirectory(install);
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(victim);
        try
        {
            Directory.CreateSymbolicLink(proofLink, victim);
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }

        var result = await Release1TrustSidecarHydrator.HydrateAsync(
            Config(),
            install,
            data,
            CancellationToken.None,
            new ThrowingHandler(),
            (_, _) => SignedReleaseCohortValidation.Reject("missing"),
            (_, _, _) => throw new InvalidOperationException("must not stage"),
            protectedStageRoot: proofLink);

        Assert.False(result.Succeeded);
        Assert.Equal("sidecar_hydration_failed", result.Code);
        Assert.Empty(Directory.EnumerateFileSystemEntries(victim));
    }

    [Fact]
    public async Task Stage_swap_after_validation_is_rejected_before_install_root_write()
    {
        var install = Path.Combine(_root, "Program Files", "Suavo", "Agent");
        var data = Path.Combine(_root, "ProgramData", "SuavoAgent");
        var proof = Path.Combine(_root, "ProgramData", "SuavoAgent-InstallerProof");
        var attacker = Path.Combine(_root, "attacker-sidecar.bin");
        Directory.CreateDirectory(install);
        Directory.CreateDirectory(data);
        Release1MsiInstallMarkerStore.CreateAndProtectProofDirectory(proof);
        File.WriteAllText(attacker, "attacker-content");
        var expectedAssets = Assets();
        string? swappedPath = null;

        var result = await Release1TrustSidecarHydrator.HydrateAsync(
            Config(),
            install,
            data,
            CancellationToken.None,
            new SidecarHandler(expectedAssets),
            (_, _) => SignedReleaseCohortValidation.Reject("missing"),
            (_, receipts, _) => AssetsMatch(receipts, expectedAssets)
                ? SignedReleaseCohortValidation.Valid(Evidence())
                : SignedReleaseCohortValidation.Reject("stage_changed"),
            protectedStageRoot: proof,
            beforePublish: stage =>
            {
                swappedPath = Path.Combine(
                    stage,
                    MaintenanceContract.ReleaseChecksumsFileName);
                File.Delete(swappedPath);
                File.CreateSymbolicLink(swappedPath, attacker);
            });

        Assert.False(result.Succeeded);
        Assert.Equal("sidecar_hydration_failed", result.Code);
        Assert.Empty(Directory.EnumerateFileSystemEntries(install));
        Assert.Equal("attacker-content", File.ReadAllText(attacker));
        Assert.NotNull(swappedPath);
    }

    [Fact]
    public async Task Failed_post_publish_validation_restores_every_previous_sidecar()
    {
        var install = Path.Combine(_root, "Program Files", "Suavo", "Agent");
        var data = Path.Combine(_root, "ProgramData", "SuavoAgent");
        var proof = Path.Combine(_root, "ProgramData", "SuavoAgent-InstallerProof");
        Directory.CreateDirectory(install);
        Directory.CreateDirectory(data);
        Release1MsiInstallMarkerStore.CreateAndProtectProofDirectory(proof);
        var expectedAssets = Assets();
        var previousAssets = expectedAssets.ToDictionary(
            pair => pair.Key,
            pair => Encoding.UTF8.GetBytes("previous-" + pair.Key),
            StringComparer.Ordinal);
        foreach (var pair in previousAssets)
            File.WriteAllBytes(Path.Combine(install, pair.Key), pair.Value);
        var installedValidationCalls = 0;

        var result = await Release1TrustSidecarHydrator.HydrateAsync(
            Config(),
            install,
            data,
            CancellationToken.None,
            new SidecarHandler(expectedAssets),
            (_, _) =>
            {
                installedValidationCalls++;
                return SignedReleaseCohortValidation.Reject(
                    installedValidationCalls == 1 ? "stale" : "forced_post_publish_failure");
            },
            (_, receipts, _) => AssetsMatch(receipts, expectedAssets)
                ? SignedReleaseCohortValidation.Valid(Evidence())
                : SignedReleaseCohortValidation.Reject("stage_changed"),
            protectedStageRoot: proof);

        Assert.False(result.Succeeded);
        Assert.Equal("sidecar_hydration_failed", result.Code);
        Assert.True(installedValidationCalls >= 2);
        Assert.All(previousAssets, pair => Assert.Equal(
            pair.Value,
            File.ReadAllBytes(Path.Combine(install, pair.Key))));
        Assert.Empty(Directory.EnumerateDirectories(proof));
    }

    private static bool AssetsMatch(
        string directory,
        IReadOnlyDictionary<string, byte[]> expected) => expected.All(pair =>
        File.Exists(Path.Combine(directory, pair.Key)) &&
        File.ReadAllBytes(Path.Combine(directory, pair.Key)).SequenceEqual(pair.Value));

    private static SetupConfig Config() => new(
        PharmacyId: "pharmacy-test",
        ApiKey: "test-hmac-key-with-at-least-32-bytes",
        CloudUrl: "https://suavollc.com",
        ReleaseTag: "v4.0.0",
        LearningMode: true,
        AgentId: "agent-test");

    private static Dictionary<string, byte[]> Assets() => new(StringComparer.Ordinal)
    {
        [MaintenanceContract.ReleaseChecksumsFileName] = "checksums"u8.ToArray(),
        [MaintenanceContract.ReleaseChecksumsSignatureFileName] = "der-signature"u8.ToArray(),
        [MaintenanceContract.FieldReleaseReceiptFileName] = "field-receipt"u8.ToArray(),
        ["update-manifest-v4.0.0.txt"] = "manifest"u8.ToArray(),
        ["update-manifest-v4.0.0.sig"] = "p1363-signature"u8.ToArray(),
    };

    private static SignedReleaseCohortEvidence Evidence() => new(
        "v4.0.0",
        new string('a', 40),
        OtaUpdateTrust.LegacyV1KeyId,
        new string('1', 64),
        new string('2', 64),
        new string('3', 64),
        new string('4', 64),
        new string('5', 64),
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["SuavoAgent.Core.exe"] = new string('6', 64),
            ["SuavoAgent.Broker.exe"] = new string('7', 64),
            ["SuavoAgent.Helper.exe"] = new string('8', 64),
            ["SuavoAgent.Watchdog.exe"] = new string('9', 64),
            [MaintenanceContract.SignedSetupArtifactName] = new string('5', 64),
        });

    private sealed class SidecarHandler(
        IReadOnlyDictionary<string, byte[]> assets) : HttpMessageHandler
    {
        internal List<string> RequestedAssets { get; } = [];
        internal List<Authorization> Authorizations { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.NotNull(request.RequestUri);
            Assert.Equal("/api/agent/release1/sidecar", request.RequestUri.AbsolutePath);
            var query = request.RequestUri.Query.TrimStart('?')
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Split('=', 2))
                .ToDictionary(
                    part => Uri.UnescapeDataString(part[0]),
                    part => Uri.UnescapeDataString(part[1]),
                    StringComparer.Ordinal);
            Assert.Equal("v4.0.0", query["releaseTag"]);
            var asset = query["asset"];
            RequestedAssets.Add(asset);
            Authorizations.Add(new(
                Header(request, "x-agent-auth-version"),
                Header(request, "x-agent-timestamp"),
                Header(request, "x-agent-nonce"),
                Header(request, "x-agent-content-sha256"),
                Header(request, "x-agent-signature")));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(assets[asset]),
            });
        }

        private static string Header(HttpRequestMessage request, string name) =>
            Assert.Single(request.Headers.GetValues(name));
    }

    private sealed class RedirectHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.Found)
            {
                Headers = { Location = new Uri("https://example.invalid/asset") },
            });
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => throw new InvalidOperationException(
            "An invalid protected stage must fail before network access.");
    }

    private sealed record Authorization(
        string Version,
        string Timestamp,
        string Nonce,
        string BodySha256,
        string Signature);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { }
    }
}
