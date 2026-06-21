// tests/SuavoAgent.Setup.Tests/Preflight/VcRedistPreflightTests.cs
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using SuavoAgent.Setup.Preflight;
using Xunit;

namespace SuavoAgent.Setup.Tests.Preflight;

public class VcRedistPreflightTests
{
    private sealed class OkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new ByteArrayContent(new byte[] { 1, 2, 3 }) });
    }

    [Fact]
    public async Task Already_present_short_circuits_without_download()
    {
        var preflight = new VcRedistPreflight(
            checker: new VcRedistChecker(fileExists: _ => true, readRegistryVersion: () => null),
            providerFactory: () => throw new System.Exception("must not download when already present"),
            installer: new VcRedistInstaller(runProcess: (_, _, _) => Task.FromResult(0)));

        var outcome = await preflight.EnsureAsync(System.IO.Path.GetTempPath(), CancellationToken.None);

        Assert.Equal(VcRedistPreflightState.AlreadyPresent, outcome.State);
    }

    [Fact]
    public async Task Missing_then_installed_reports_Installed()
    {
        var installed = false;
        var checker = new VcRedistChecker(fileExists: _ => installed, readRegistryVersion: () => null);
        var preflight = new VcRedistPreflight(
            checker: checker,
            providerFactory: () => new VcRedistProvider(
                new HttpClient(new OkHandler()),
                "https://example/vc_redist.x64.exe",
                System.Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(new byte[] { 1, 2, 3 })).ToLowerInvariant()),
            installer: new VcRedistInstaller(
                runProcess: (_, _, _) => { installed = true; return Task.FromResult(0); },
                checker: checker));

        var outcome = await preflight.EnsureAsync(System.IO.Path.GetTempPath(), CancellationToken.None);

        Assert.Equal(VcRedistPreflightState.Installed, outcome.State);
    }
}
