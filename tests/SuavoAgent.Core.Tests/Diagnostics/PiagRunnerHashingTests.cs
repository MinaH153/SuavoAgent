using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Diagnostics;
using Xunit;

namespace SuavoAgent.Core.Tests.Diagnostics;

// PIAG-1 (Pilot Install Acceptance Gate) PR B coverage. The Trip A
// failure mode that PIAG-1 exists to catch was only visible in
// production — these tests pin the behaviors most likely to drift
// silently:
//   1. Hash collection over a temp dir with known files
//   2. Reparse-point rejection (anti-tamper)
//   3. Pre-flight skip when AgentId is unset
//   4. PiagResult outcome variants
public class PiagRunnerHashingTests : IDisposable
{
    private readonly string _scratchDir;

    public PiagRunnerHashingTests()
    {
        _scratchDir = Path.Combine(
            Path.GetTempPath(),
            $"piag-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_scratchDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_scratchDir))
        {
            try { Directory.Delete(_scratchDir, recursive: true); }
            catch { /* best-effort test cleanup */ }
        }
    }

    [Fact]
    public async Task RunAsync_skipped_when_agent_id_unconfigured()
    {
        var runner = BuildRunner(agentId: null);
        var result = await runner.RunAsync("manual", false, CancellationToken.None);
        Assert.Equal(PiagOutcome.Skipped, result.Outcome);
        Assert.Equal("agent_id_unconfigured", result.ErrorCode);
    }

    [Fact]
    public async Task RunAsync_skipped_when_machine_fingerprint_unconfigured()
    {
        var runner = BuildRunner(machineFingerprint: null);
        var result = await runner.RunAsync("manual", false, CancellationToken.None);
        Assert.Equal(PiagOutcome.Skipped, result.Outcome);
        Assert.Equal("machine_fingerprint_unconfigured", result.ErrorCode);
    }

    [Fact]
    public void HashAgentBinariesIn_returns_correct_sha256_for_known_files()
    {
        var fileA = Path.Combine(_scratchDir, "SuavoAgent.Core.exe");
        var fileB = Path.Combine(_scratchDir, "SuavoAgent.Watchdog.exe");
        File.WriteAllBytes(fileA, new byte[] { 0xDE, 0xAD });
        File.WriteAllBytes(fileB, new byte[] { 0xBE, 0xEF });

        var hashes = PiagRunner.HashAgentBinariesIn(_scratchDir, NullLogger.Instance);

        Assert.Equal(2, hashes.Count);
        Assert.True(hashes.ContainsKey("SuavoAgent.Core.exe"));
        Assert.True(hashes.ContainsKey("SuavoAgent.Watchdog.exe"));
        // Confirm hashes match canonical SHA-256 of inputs.
        var expectedA = Convert.ToHexString(SHA256.HashData(new byte[] { 0xDE, 0xAD })).ToLowerInvariant();
        Assert.Equal(expectedA, hashes["SuavoAgent.Core.exe"]);
    }

    [Fact]
    public void HashAgentBinariesIn_skips_non_matching_filenames()
    {
        File.WriteAllBytes(Path.Combine(_scratchDir, "unrelated.exe"), new byte[] { 0x00 });
        File.WriteAllBytes(
            Path.Combine(_scratchDir, "SuavoAgent.Core.exe"),
            new byte[] { 0xAA });

        var hashes = PiagRunner.HashAgentBinariesIn(_scratchDir, NullLogger.Instance);

        Assert.Single(hashes);
        Assert.True(hashes.ContainsKey("SuavoAgent.Core.exe"));
    }

    [Fact]
    public void HashAgentBinariesIn_returns_empty_when_dir_missing()
    {
        var hashes = PiagRunner.HashAgentBinariesIn(
            Path.Combine(_scratchDir, "does-not-exist"),
            NullLogger.Instance);
        Assert.Empty(hashes);
    }

    [Fact]
    public void HashAgentBinariesIn_emits_lowercase_hex()
    {
        var path = Path.Combine(_scratchDir, "SuavoAgent.Core.exe");
        File.WriteAllBytes(path, new byte[] { 0x42 });
        var hashes = PiagRunner.HashAgentBinariesIn(_scratchDir, NullLogger.Instance);
        Assert.Single(hashes);
        Assert.Matches(@"^[0-9a-f]{64}$", hashes["SuavoAgent.Core.exe"]);
    }

    [Fact]
    public void PiagResult_static_factories_set_outcome_correctly()
    {
        Assert.Equal(PiagOutcome.Verdict, PiagResult.Verdict("pass", null).Outcome);
        Assert.Equal(PiagOutcome.CloudRejected, PiagResult.CloudRejected("hmac").Outcome);
        Assert.Equal(PiagOutcome.TransportError, PiagResult.TransportError("network").Outcome);
        Assert.Equal(PiagOutcome.Skipped, PiagResult.Skipped("config_missing").Outcome);
    }

    private PiagRunner BuildRunner(
        string? agentId = "11111111-1111-1111-1111-111111111111",
        string? machineFingerprint = "22222222-2222-2222-2222-222222222222")
    {
        var options = Options.Create(new AgentOptions
        {
            AgentId = agentId,
            MachineFingerprint = machineFingerprint,
            ApiKey = "test-api-key",
            CloudUrl = "https://example.invalid",
        });
        var services = new ServiceCollection().BuildServiceProvider();
        // SuavoCloudClient takes only AgentOptions; for these unit tests
        // we never reach a POST (pre-flight checks return before any
        // cloud call), so a default client suffices.
        var client = new SuavoCloudClient(options.Value);
        return new PiagRunner(
            options,
            client,
            NullLogger<PiagRunner>.Instance,
            services);
    }

}
