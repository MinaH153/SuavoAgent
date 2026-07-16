using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using SuavoAgent.Contracts.Vision;
using SuavoAgent.Setup.Security;
using Xunit;

namespace SuavoAgent.Setup.Tests.Security;

public sealed class ReleaseOcrCohortProvisionerTests : IDisposable
{
    private const string BundleUrl = "https://assets.example/reviewed.nupkg";
    private const string TrainedDataUrl = "https://assets.example/eng.traineddata";
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "suavo-setup-ocr-" + Guid.NewGuid().ToString("N"));

    public ReleaseOcrCohortProvisionerTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Exact_release_inventory_is_validated_before_live_tree_and_locked_twice()
    {
        var fixture = Fixture();
        var aclCalls = 0;

        var result = await ReleaseOcrCohortProvisioner.ProvisionOneForTestsAsync(
            _root,
            fixture.Cohort,
            path =>
            {
                Assert.Equal(Target(fixture.Cohort), path);
                aclCalls++;
                return true;
            },
            new RouteHandler(fixture.Routes));

        Assert.True(result.Succeeded, result.Code);
        Assert.Equal("vision_release_cohort_provisioned", result.Code);
        Assert.Equal(2, aclCalls);
        Assert.False(Directory.Exists(Path.Combine(Target(fixture.Cohort), "x86")));
        Assert.False(Directory.Exists(Path.Combine(Target(fixture.Cohort), "lib")));
        Assert.True(ReleaseOcrCohortCatalog.VerifyInstalledAt(
            Target(fixture.Cohort),
            CohortsRoot(),
            fixture.Cohort));
    }

    [Fact]
    public async Task Valid_existing_cohort_is_idempotent_without_network_and_reasserts_acl()
    {
        var fixture = Fixture();
        Assert.True((await ReleaseOcrCohortProvisioner.ProvisionOneForTestsAsync(
            _root,
            fixture.Cohort,
            _ => true,
            new RouteHandler(fixture.Routes))).Succeeded);
        var aclCalls = 0;

        var replay = await ReleaseOcrCohortProvisioner.ProvisionOneForTestsAsync(
            _root,
            fixture.Cohort,
            _ =>
            {
                aclCalls++;
                return true;
            },
            new ThrowingHandler());

        Assert.True(replay.Succeeded, replay.Code);
        Assert.Equal("vision_release_cohort_already_provisioned", replay.Code);
        Assert.Equal(1, aclCalls);
    }

    [Fact]
    public async Task Final_reassert_locks_the_highest_replaceable_vision_ancestor()
    {
        var fixture = Fixture();
        Assert.True((await ReleaseOcrCohortProvisioner.ProvisionOneForTestsAsync(
            _root,
            fixture.Cohort,
            _ => true,
            new RouteHandler(fixture.Routes))).Succeeded);
        string? lockedPath = null;

        var reasserted = ReleaseOcrCohortProvisioner.ReassertInstalledCohortAclsForTests(
            _root,
            path =>
            {
                lockedPath = path;
                return true;
            },
            [fixture.Cohort]);

        Assert.True(reasserted);
        Assert.Equal(Path.Combine(_root, "vision"), lockedPath);
    }

    [Fact]
    public async Task Bundle_hash_mismatch_never_creates_live_executable_tree()
    {
        var fixture = Fixture();
        var routes = fixture.Routes.ToDictionary(item => item.Key, item => item.Value);
        routes[BundleUrl] = fixture.Routes[BundleUrl].Select(value => (byte)(value ^ 1)).ToArray();
        var aclCalls = 0;

        var result = await ReleaseOcrCohortProvisioner.ProvisionOneForTestsAsync(
            _root,
            fixture.Cohort,
            _ =>
            {
                aclCalls++;
                return true;
            },
            new RouteHandler(routes));

        Assert.False(result.Succeeded);
        Assert.Equal("vision_release_cohort_bundle_mismatch", result.Code);
        Assert.Equal(0, aclCalls);
        Assert.False(Directory.Exists(Target(fixture.Cohort)));
    }

    [Fact]
    public async Task Existing_untrusted_tree_is_never_recursively_deleted()
    {
        var fixture = Fixture();
        var target = Target(fixture.Cohort);
        Directory.CreateDirectory(target);
        var marker = Path.Combine(target, "must-survive.txt");
        File.WriteAllText(marker, "untrusted");

        var result = await ReleaseOcrCohortProvisioner.ProvisionOneForTestsAsync(
            _root,
            fixture.Cohort,
            _ => false,
            new RouteHandler(fixture.Routes));

        Assert.False(result.Succeeded);
        Assert.Equal("vision_release_cohort_existing_tree_untrusted", result.Code);
        Assert.Equal("untrusted", File.ReadAllText(marker));
    }

    [Fact]
    public async Task Trained_data_mismatch_rejects_the_whole_cohort_before_live_creation()
    {
        var fixture = Fixture();
        var routes = fixture.Routes.ToDictionary(item => item.Key, item => item.Value);
        routes[TrainedDataUrl] = new byte[] { 9, 9, 9, 9 };

        var result = await ReleaseOcrCohortProvisioner.ProvisionOneForTestsAsync(
            _root,
            fixture.Cohort,
            _ => true,
            new RouteHandler(routes));

        Assert.False(result.Succeeded);
        Assert.Equal("vision_release_cohort_inventory_mismatch", result.Code);
        Assert.False(Directory.Exists(Target(fixture.Cohort)));
    }

    private FixtureData Fixture()
    {
        var tesseract = new byte[] { 1, 2, 3, 4 };
        var leptonica = new byte[] { 5, 6, 7 };
        var trainedData = new byte[] { 8, 9, 10, 11 };
        var package = Zip(new Dictionary<string, byte[]>
        {
            ["x64/tesseract50.dll"] = tesseract,
            ["x64/leptonica-1.82.0.dll"] = leptonica,
            ["x86/tesseract50.dll"] = new byte[] { 99 },
            ["lib/netstandard2.0/Tesseract.dll"] = new byte[] { 98 },
            ["_rels/.rels"] = new byte[] { 97 },
        });
        var cohort = ReleaseOcrCohort.Create(
            BundleUrl,
            Sha(package),
            package.LongLength,
            new ReleaseOcrFile[]
            {
                new("x64/tesseract50.dll", tesseract.LongLength, Sha(tesseract)),
                new("x64/leptonica-1.82.0.dll", leptonica.LongLength, Sha(leptonica)),
                new(
                    "tessdata/eng.traineddata",
                    trainedData.LongLength,
                    Sha(trainedData)),
            },
            new ReleaseOcrTrainedDataSource(
                TrainedDataUrl,
                Sha(trainedData),
                trainedData.LongLength,
                "tessdata/eng.traineddata"));
        return new(
            cohort,
            new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                [BundleUrl] = package,
                [TrainedDataUrl] = trainedData,
            });
    }

    private string CohortsRoot() => Path.Combine(_root, "vision", "cohorts");
    private string Target(ReleaseOcrCohort cohort) =>
        Path.Combine(CohortsRoot(), cohort.BundleSha256);

    private static byte[] Zip(IReadOnlyDictionary<string, byte[]> files)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in files)
            {
                using var stream = archive.CreateEntry(file.Key).Open();
                stream.Write(file.Value);
            }
        }
        return output.ToArray();
    }

    private static string Sha(byte[] value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed record FixtureData(
        ReleaseOcrCohort Cohort,
        IReadOnlyDictionary<string, byte[]> Routes);

    private sealed class RouteHandler(IReadOnlyDictionary<string, byte[]> routes)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.AbsoluteUri;
            if (url is null || !routes.TryGetValue(url, out var value))
                throw new InvalidOperationException("Unexpected route");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(value),
            });
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Network must not be reached");
    }
}
