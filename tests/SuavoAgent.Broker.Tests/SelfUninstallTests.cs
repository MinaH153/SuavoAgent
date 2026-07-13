using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Broker;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Contracts.Security;
using SuavoAgent.Diagnostics.Maintenance;
using Xunit;

namespace SuavoAgent.Broker.Tests;

public sealed class SelfUninstallTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "suavo-self-uninstall-broker-" + Guid.NewGuid().ToString("N"));
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly InMemoryMaintenanceAttestationKeyProvider _maintenanceKeys = new();
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;
    private const string KeyId = "test-command-key";
    private const string AgentId = "11111111-1111-4111-8111-111111111111";
    private const string Fingerprint = "fingerprint-1";
    private const string CommandId = "33333333-3333-4333-8333-333333333333";
    private const string Nonce = "44444444-4444-4444-8444-444444444444";

    public void Dispose()
    {
        _key.Dispose();
        _maintenanceKeys.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void BuildMaintenanceStartInfo_UsesPreserveDataAndNoInheritedHandles()
    {
        var stagingDir = Path.Combine(_root, "protected-staging");
        var staged = new PrivilegedStagedExecutable(
            stagingDir,
            Path.Combine(
                stagingDir,
                PrivilegedExecutableStaging.UninstallFilePrefix +
                new string('a', 32) + ".exe"),
            new string('b', 64));

        var startInfo = SelfUninstall.BuildMaintenanceStartInfo(staged);

        Assert.Equal(staged.ExecutablePath, startInfo.FileName);
        Assert.Equal(stagingDir, startInfo.WorkingDirectory);
        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.CreateNoWindow);
        Assert.False(startInfo.RedirectStandardInput);
        Assert.False(startInfo.RedirectStandardOutput);
        Assert.False(startInfo.RedirectStandardError);
        Assert.Equal(
            new[]
            {
                MaintenanceContract.UninstallSwitch,
                SelfUninstall.SilentSwitch,
                SelfUninstallContract.PreserveDataSwitch,
                MaintenanceContract.ProtectedStagingSwitch,
            },
            startInfo.ArgumentList);
        Assert.DoesNotContain(SelfUninstallContract.PurgeRetainedDataSwitch, startInfo.ArgumentList);
    }

    [Fact]
    public void Authenticated_launch_passes_exact_claim_path_without_deleting_authority()
    {
        var stagingDir = Path.Combine(_root, "protected-staging");
        var claimPath = Path.Combine(_root, "uninstall.request.claimed");
        var staged = new PrivilegedStagedExecutable(
            stagingDir,
            Path.Combine(
                stagingDir,
                PrivilegedExecutableStaging.UninstallFilePrefix +
                new string('a', 32) + ".exe"),
            new string('b', 64));

        var startInfo = SelfUninstall.BuildMaintenanceStartInfo(staged, claimPath);

        Assert.Equal(
            new[]
            {
                MaintenanceContract.UninstallSwitch,
                SelfUninstall.SilentSwitch,
                SelfUninstallContract.PreserveDataSwitch,
                MaintenanceContract.ProtectedStagingSwitch,
                SelfUninstallContract.AuthenticatedRequestSwitch,
                Path.GetFullPath(claimPath),
            },
            startInfo.ArgumentList);
    }

    [Fact]
    public void ValidRequest_IsAtomicallyClaimedVerifiedAndLaunched()
    {
        var (requestPath, installDir) = Arrange(CreateRequest());
        ProcessStartInfo? captured = null;

        var status = Act(
            requestPath,
            installDir,
            launch: startInfo =>
            {
                captured = startInfo;
                return true;
            });

        Assert.Equal(SelfUninstallLaunchStatus.LaunchAccepted, status);
        Assert.NotNull(captured);
        Assert.NotEqual(
            SelfUninstall.MaintenanceExecutablePath(installDir),
            captured.FileName);
        Assert.StartsWith(
            Path.Combine(_root, "protected-staging-"),
            captured.FileName,
            StringComparison.Ordinal);
        Assert.Contains(
            MaintenanceContract.ProtectedStagingSwitch,
            captured.ArgumentList);
        Assert.False(File.Exists(requestPath));
        Assert.True(File.Exists(SelfUninstall.ClaimedRequestPath(requestPath)));
        Assert.True(File.Exists(SelfUninstallAcceptanceContract.PathForClaim(
            SelfUninstall.ClaimedRequestPath(requestPath))));
        Assert.Contains(
            Path.GetFullPath(SelfUninstall.ClaimedRequestPath(requestPath)),
            captured.ArgumentList);
    }

    [Fact]
    public void MalformedRequest_NeverLaunchesAndRawContentIsNotLogged()
    {
        Directory.CreateDirectory(_root);
        var requestPath = Path.Combine(_root, SelfUninstallContract.RequestFileName);
        var installDir = Path.Combine(_root, "install");
        Directory.CreateDirectory(installDir);
        File.WriteAllText(requestPath, "Patient Jane Secret malformed payload");
        File.WriteAllText(
            Path.Combine(installDir, MaintenanceContract.ExecutableName),
            "test");
        var logger = new RecordingLogger();
        var launchCalls = 0;

        var status = Act(
            requestPath,
            installDir,
            logger,
            _ =>
            {
                launchCalls++;
                return true;
            });

        Assert.Equal(SelfUninstallLaunchStatus.RequestRejected, status);
        Assert.Equal(0, launchCalls);
        Assert.DoesNotContain(
            logger.Messages,
            message => message.Contains("Patient Jane Secret", StringComparison.Ordinal));
    }

    [Fact]
    public void StaleRequest_NeverLaunches()
    {
        var (requestPath, installDir) = Arrange(CreateRequest(_now.AddMinutes(-10)));

        var status = Act(requestPath, installDir);

        Assert.Equal(SelfUninstallLaunchStatus.RequestRejected, status);
    }

    [Fact]
    public void DurableAcceptanceSurvivesBrokerDelayRestartAndCloudAuthorityExpiry()
    {
        var (requestPath, installDir) = Arrange(CreateRequest());
        var first = Act(requestPath, installDir, launch: _ => false);
        Assert.Equal(SelfUninstallLaunchStatus.LaunchFailed, first);
        var claim = SelfUninstall.ClaimedRequestPath(requestPath);
        Assert.True(File.Exists(SelfUninstallAcceptanceContract.PathForClaim(claim)));

        var second = Act(
            requestPath,
            installDir,
            launch: _ => true,
            now: _now.AddMinutes(10));

        Assert.Equal(SelfUninstallLaunchStatus.LaunchAccepted, second);
    }

    [Fact]
    public void WrongCommandKey_NeverLaunches()
    {
        var request = CreateRequest() with { KeyId = "wrong-key" };
        var (requestPath, installDir) = Arrange(request);

        var status = Act(requestPath, installDir);

        Assert.Equal(SelfUninstallLaunchStatus.RequestRejected, status);
    }

    [Fact]
    public void MismatchedDataHash_NeverLaunches()
    {
        var request = CreateRequest() with { DataJson = "{\"commandId\":\"other\"}" };
        var (requestPath, installDir) = Arrange(request);

        var status = Act(requestPath, installDir);

        Assert.Equal(SelfUninstallLaunchStatus.RequestRejected, status);
    }

    [Fact]
    public void MismatchedArchiveReceipt_NeverLaunches()
    {
        var request = CreateRequest() with { ArchiveDigest = new string('a', 64) };
        var (requestPath, installDir) = Arrange(request);

        var status = Act(requestPath, installDir);

        Assert.Equal(SelfUninstallLaunchStatus.RequestRejected, status);
    }

    [Fact]
    public void UntrustedMaintenanceHost_NeverLaunchesAndClaimRemainsRecoverable()
    {
        var (requestPath, installDir) = Arrange(CreateRequest());
        var launchCalls = 0;

        var status = Act(
            requestPath,
            installDir,
            launch: _ =>
            {
                launchCalls++;
                return true;
            },
            trust: _ => new MaintenanceHostTrustResult(
                false,
                MaintenanceTrustSource.None,
                "signed_receipt_missing"));

        Assert.Equal(SelfUninstallLaunchStatus.MaintenanceUntrusted, status);
        Assert.Equal(0, launchCalls);
        Assert.False(File.Exists(requestPath));
        Assert.True(File.Exists(SelfUninstall.ClaimedRequestPath(requestPath)));
    }

    [Fact]
    public void InstallTreeSwapAfterSignedTrustNeverBecomesLaunchedBytes()
    {
        var (requestPath, installDir) = Arrange(CreateRequest());
        var maintenancePath = SelfUninstall.MaintenanceExecutablePath(installDir);
        var signedHash = Sha256(maintenancePath);
        var launchCalls = 0;

        var status = Act(
            requestPath,
            installDir,
            launch: _ =>
            {
                launchCalls++;
                return true;
            },
            trust: path =>
            {
                Assert.Equal(maintenancePath, path);
                File.WriteAllText(path, "attacker-replacement");
                return new MaintenanceHostTrustResult(
                    true,
                    MaintenanceTrustSource.SignedReleaseChecksums,
                    "trusted",
                    signedHash);
            });

        Assert.Equal(SelfUninstallLaunchStatus.MaintenanceStagingFailed, status);
        Assert.Equal(0, launchCalls);
        Assert.True(File.Exists(SelfUninstall.ClaimedRequestPath(requestPath)));
    }

    [Fact]
    public void TrustedReceiptWithoutExactExecutableDigestNeverStagesOrLaunches()
    {
        var (requestPath, installDir) = Arrange(CreateRequest());
        var stageCalls = 0;
        var launchCalls = 0;

        var status = Act(
            requestPath,
            installDir,
            launch: _ =>
            {
                launchCalls++;
                return true;
            },
            trust: _ => new MaintenanceHostTrustResult(
                true,
                MaintenanceTrustSource.SignedReleaseChecksums,
                "trusted"),
            stage: (_, _) =>
            {
                stageCalls++;
                throw new InvalidOperationException("must not stage");
            });

        Assert.Equal(SelfUninstallLaunchStatus.MaintenanceUntrusted, status);
        Assert.Equal(0, stageCalls);
        Assert.Equal(0, launchCalls);
    }

    [Fact]
    public void InstalledIdentity_UsesAgentIdFromConfigButFingerprintFromMachineAuthority()
    {
        var installDir = Path.Combine(_root, "identity-install");
        Directory.CreateDirectory(installDir);
        File.WriteAllText(
            Path.Combine(installDir, "appsettings.json"),
            """
            {"Agent":{"AgentId":"11111111-1111-4111-8111-111111111111","MachineFingerprint":"attacker-controlled","MaintenanceAttestationKeyId":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","ApiKey":"do-not-log"}}
            """);

        var ok = SelfUninstall.TryLoadInstalledIdentity(
            installDir,
            () => Fingerprint,
            out var agentId,
            out var fingerprint,
            out var maintenanceKeyId);

        Assert.True(ok);
        Assert.Equal(AgentId, agentId);
        Assert.Equal(Fingerprint, fingerprint);
        Assert.Equal(new string('a', 64), maintenanceKeyId);
    }

    private (string RequestPath, string InstallDir) Arrange(SelfUninstallRequest request)
    {
        Directory.CreateDirectory(_root);
        var requestPath = Path.Combine(_root, SelfUninstallContract.RequestFileName);
        var installDir = Path.Combine(_root, "install");
        Directory.CreateDirectory(installDir);
        File.WriteAllText(requestPath, SelfUninstallContract.Serialize(request));
        File.WriteAllText(
            Path.Combine(installDir, MaintenanceContract.ExecutableName),
            "test-maintenance-host");
        return (requestPath, installDir);
    }

    private SelfUninstallLaunchStatus Act(
        string requestPath,
        string installDir,
        ILogger? logger = null,
        Func<ProcessStartInfo, bool>? launch = null,
        Func<string, MaintenanceHostTrustResult>? trust = null,
        Func<string, string, PrivilegedStagedExecutable>? stage = null,
        Func<string, string, bool>? verifyStaged = null,
        Action<string?, string?>? cleanupStaged = null,
        DateTimeOffset? now = null) =>
        SelfUninstall.TryClaimAuthenticatedRequestAndLaunch(
            requestPath,
            installDir,
            AgentId,
            Fingerprint,
            _maintenanceKeys.OpenOrCreate(Fingerprint).Enrollment.KeyId,
            _maintenanceKeys,
            Keys,
            now ?? _now,
            logger ?? NullLogger.Instance,
            launch ?? (_ => true),
            trust ?? (path => new MaintenanceHostTrustResult(
                true,
                MaintenanceTrustSource.SignedReleaseChecksums,
                "trusted",
                Sha256(path))),
            stage ?? StageForTest,
            verifyStaged ?? ((path, expected) =>
                string.Equals(Sha256(path), expected, StringComparison.Ordinal)),
            cleanupStaged ?? CleanupStagedForTest);

    private PrivilegedStagedExecutable StageForTest(
        string source,
        string expectedSha256)
    {
        if (!string.Equals(Sha256(source), expectedSha256, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("source digest changed");
        var directory = Path.Combine(
            _root,
            "protected-staging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(
            directory,
            PrivilegedExecutableStaging.UninstallFilePrefix +
            Guid.NewGuid().ToString("N") + ".exe");
        File.Copy(source, destination, overwrite: false);
        if (!string.Equals(Sha256(destination), expectedSha256, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("staged digest changed");
        return new(directory, destination, expectedSha256);
    }

    private static void CleanupStagedForTest(string? directory, string? executable)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(executable)) File.Delete(executable);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.Delete(directory);
        }
        catch { }
    }

    private static string Sha256(string path) => Convert.ToHexString(
        SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private SelfUninstallRequest CreateRequest(DateTimeOffset? timestamp = null)
    {
        var commandTimestamp = (timestamp ?? _now).ToString("O");
        var dataJson =
            $"{{\"commandId\":\"{CommandId}\",\"expiresAt\":\"{_now.AddMinutes(4):O}\"}}";
        var dataHash = RemoteCommandTrust.ComputeSha256Hex(dataJson);
        var commandSignature = Sign(RemoteCommandTrust.BuildCommandCanonical(
            SelfUninstallContract.CommandName,
            AgentId,
            Fingerprint,
            commandTimestamp,
            Nonce,
            dataHash));
        var digest = RemoteCommandTrust.ComputeSha256Hex("archive");
        var receipt = new SelfUninstallArchiveReceipt(
            "55555555-5555-4555-8555-555555555555",
            digest,
            _now.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
            Nonce,
            KeyId,
            string.Empty);
        receipt = receipt with
        {
            Signature = Sign(RemoteCommandTrust.BuildArchiveReceiptCanonical(
                receipt,
                AgentId,
                Fingerprint,
                CommandId,
                Nonce)),
        };

        return new SelfUninstallRequest(
            SelfUninstallContract.SchemaVersion,
            SelfUninstallContract.CommandName,
            AgentId,
            Fingerprint,
            commandTimestamp,
            Nonce,
            KeyId,
            commandSignature,
            dataJson,
            dataHash,
            CommandId,
            _now.ToString("O"),
            digest,
            receipt);
    }

    private IReadOnlyDictionary<string, string> Keys =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [KeyId] = Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo()),
        };

    private string Sign(string canonical) => Convert.ToBase64String(
        _key.SignData(Encoding.UTF8.GetBytes(canonical), HashAlgorithmName.SHA256));

    private sealed class RecordingLogger : ILogger
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
