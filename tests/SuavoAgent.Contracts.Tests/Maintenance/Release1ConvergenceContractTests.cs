using System.Text;
using SuavoAgent.Contracts.Maintenance;
using Xunit;

namespace SuavoAgent.Contracts.Tests.Maintenance;

public sealed class Release1ConvergenceContractTests
{
    [Fact]
    public void Canonical_install_receipt_is_sorted_compact_and_lf_terminated()
    {
        var receipt = Receipt();

        var canonical = Encoding.UTF8.GetString(
            Release1ConvergenceContract.CanonicalBytes(receipt));
        var expected =
            "{\"bootIdAtInstall\":\"" + new string('b', 64) +
            "\",\"checksumsSha256\":\"" + new string('c', 64) +
            "\",\"checksumsSignatureSha256\":\"" + new string('8', 64) +
            "\",\"hostDigest\":\"" + new string('a', 64) +
            "\",\"installCompletedAtUtc\":\"2026-07-15T12:00:00Z\"" +
            ",\"installMode\":\"full-reinstall\"" +
            ",\"installTransactionId\":\"" + new string('7', 64) +
            "\",\"installedCohort\":{" +
            "\"SuavoAgent.Broker.exe\":\"" + new string('2', 64) +
            "\",\"SuavoAgent.Core.exe\":\"" + new string('1', 64) +
            "\",\"SuavoAgent.Helper.exe\":\"" + new string('3', 64) +
            "\",\"SuavoAgent.Watchdog.exe\":\"" + new string('4', 64) +
            "\",\"SuavoSetup.exe\":\"" + new string('5', 64) +
            "\"},\"installedReleaseTag\":\"v4.0.0\"" +
            ",\"installedSourceSha\":\"" + new string('d', 40) +
            "\",\"installerArtifactSha256\":\"" + new string('e', 64) +
            "\",\"installerType\":\"msi\"" +
            ",\"maintenanceKeyId\":\"" + new string('9', 64) +
            "\",\"purpose\":\"suavoagent-release1-full-installer-receipt\"" +
            ",\"releaseReceiptSha256\":\"" + new string('f', 64) +
            "\",\"schemaVersion\":1}\n";

        Assert.Equal(expected, canonical);
        Assert.Equal(
            "fe2988cb3f1ee9ebb1df7de3976207c5b8a2aa7b3e87ef8da9d5634dbb69d990",
            Release1ConvergenceContract.CanonicalSha256(receipt));
        Assert.EndsWith("\n", canonical, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", canonical, StringComparison.Ordinal);
        Assert.DoesNotContain(": ", canonical, StringComparison.Ordinal);
        Assert.StartsWith(
            "{\"bootIdAtInstall\":\"" + new string('b', 64) +
            "\",\"checksumsSha256\":\"" + new string('c', 64),
            canonical,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"installedCohort\":{" +
            "\"SuavoAgent.Broker.exe\":\"" + new string('2', 64) + "\"," +
            "\"SuavoAgent.Core.exe\":\"" + new string('1', 64),
            canonical,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Windows_boot_identifier_is_stable_across_time_shifts_and_bucket_edges()
    {
        var host = Release1ConvergenceContract.HostDigest("machine-guid-test");
        var observations = new[]
        {
            DateTimeOffset.Parse("2026-07-15T12:00:29.999Z"),
            DateTimeOffset.Parse("2026-07-15T12:00:30.001Z"),
            DateTimeOffset.Parse("2026-07-15T19:00:30.001Z"),
        };
        var sameBoot = observations
            .Select(_ => Release1ConvergenceContract.WindowsBootToken(71))
            .Select(token => Release1ConvergenceContract.BootIdFromToken(
                "machine-guid-test",
                token))
            .ToArray();
        var nextBoot = Release1ConvergenceContract.BootIdFromToken(
            "machine-guid-test",
            Release1ConvergenceContract.WindowsBootToken(72));

        Assert.Matches("^[a-f0-9]{64}$", host);
        Assert.Single(sameBoot.Distinct(StringComparer.Ordinal));
        Assert.NotEqual(sameBoot[0], nextBoot);
        Assert.DoesNotContain("machine", host, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("71")]
    [InlineData(-1L)]
    public void Missing_or_non_native_windows_boot_identifier_fails_closed(object? value)
    {
        var error = Assert.Throws<Release1BootIdentityUnavailableException>(() =>
            Release1ConvergenceContract.WindowsBootToken(value));

        Assert.Contains("refused", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Canonical_digest_changes_when_any_install_binding_changes()
    {
        var receipt = Receipt();
        var original = Release1ConvergenceContract.CanonicalSha256(receipt);
        var changed = Release1ConvergenceContract.CanonicalSha256(
            receipt with { InstalledSourceSha = new string('f', 40) });

        Assert.Matches("^[a-f0-9]{64}$", original);
        Assert.NotEqual(original, changed);
    }

    [Theory]
    [InlineData("v4.0.0", "SuavoAgent-v4.0.0-win-x64.msi")]
    [InlineData("4.0.0", "SuavoAgent-v4.0.0-win-x64.msi")]
    [InlineData("V4.0.0", "SuavoAgent-v4.0.0-win-x64.msi")]
    public void Release_msi_artifact_name_is_canonical(
        string releaseTag,
        string expected) => Assert.Equal(
        expected,
        Release1ConvergenceContract.ReleaseMsiArtifactName(releaseTag));

    private static Release1InstallReceipt Receipt() => new(
        1,
        Release1ConvergenceContract.InstallReceiptPurpose,
        new string('a', 64),
        new string('9', 64),
        "v4.0.0",
        new string('d', 40),
        Release1ConvergenceContract.MsiInstallerType,
        new string('e', 64),
        new string('f', 64),
        new string('c', 64),
        new string('8', 64),
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["SuavoAgent.Core.exe"] = new string('1', 64),
            ["SuavoAgent.Broker.exe"] = new string('2', 64),
            ["SuavoAgent.Helper.exe"] = new string('3', 64),
            ["SuavoAgent.Watchdog.exe"] = new string('4', 64),
            [MaintenanceContract.SignedSetupArtifactName] = new string('5', 64),
        },
        new string('7', 64),
        "2026-07-15T12:00:00Z",
        new string('b', 64),
        Release1ConvergenceContract.FullReinstallMode);
}
