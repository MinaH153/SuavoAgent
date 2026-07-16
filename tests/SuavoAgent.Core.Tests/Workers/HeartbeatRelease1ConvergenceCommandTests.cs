using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Contracts.Security;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.State;
using SuavoAgent.Core.Workers;
using Xunit;

namespace SuavoAgent.Core.Tests.Workers;

public sealed class HeartbeatRelease1ConvergenceCommandTests : IDisposable
{
    private const string AgentId = "11111111-1111-4111-8111-111111111111";
    private const string Fingerprint = "heartbeat-release1-host";
    private const string CommandKeyId = "test-command-key";
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "suavo-heartbeat-release1-" + Guid.NewGuid().ToString("N"));
    private readonly ECDsa _commandKey = ECDsa.Create(
        ECCurve.NamedCurves.nistP256);
    private readonly ECDsa _maintenanceKey = ECDsa.Create(
        ECCurve.NamedCurves.nistP256);
    private readonly InMemoryDeviceAttestationKeyProvider _deviceKeys = new();

    [Fact]
    public async Task ExactSignedChallengeRegistersBeforeNonceAndStartsDurableFlow()
    {
        Directory.CreateDirectory(_root);
        var dbPath = Path.Combine(_root, "state.db");
        var receiptPath = Path.Combine(
            _root,
            Release1ConvergenceContract.InstallReceiptFileName);
        using var pending = _deviceKeys.OpenOrCreate(Fingerprint);
        var options = new AgentOptions
        {
            AgentId = AgentId,
            MachineFingerprint = Fingerprint,
            Version = "4.0.0",
            DeviceAttestationKeyId = pending.Enrollment.KeyId,
            DeviceAttestationKeyName = pending.LocalKeyName,
            MaintenanceAttestationKeyId = MaintenanceKeyId(),
        };
        _deviceKeys.CommitPending(Fingerprint, pending.Enrollment.KeyId);
        var now = DateTimeOffset.UtcNow;
        WriteReceipt(receiptPath, options, now);
        using var db = new AgentStateDb(dbPath);
        using var deviceSigner = new DeviceAuthoritySigner(options, _deviceKeys);
        var transport = new ChallengeTransport();
        var coordinator = new Release1ConvergenceCoordinator(
            db,
            options,
            deviceSigner,
            transport,
            NullLogger.Instance,
            receiptPath,
            () => now,
            () => new string('e', 64));
        var services = new ServiceCollection()
            .AddSingleton(db)
            .AddSingleton(coordinator)
            .AddSingleton<IDeviceAuthoritySigner>(deviceSigner)
            .AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>),
                typeof(NullLogger<>))
            .BuildServiceProvider();
        var worker = new HeartbeatWorker(
            NullLogger<HeartbeatWorker>.Instance,
            Options.Create(options),
            services,
            db);
        SetVerifier(worker);
        var commandId = "22222222-2222-4222-8222-222222222222";
        var nonce = "33333333-3333-4333-8333-333333333333";
        var expiresAt = Release1ConvergenceContract.ExactUtc(now.AddHours(1));
        var data = new
        {
            commandId,
            inventorySha256 = new string('4', 64),
            bridgeReleaseTag = "v4.0.0",
            bridgeSourceSha = new string('5', 40),
            expiresAt,
        };
        var response = SignedResponse(data, nonce, now);

        await InvokeProcessAsync(worker, response);

        Assert.NotNull(db.GetRelease1Challenge(commandId));
        Assert.NotNull(db.GetRelease1Delivery(commandId, "challenge_ack"));
        Assert.NotNull(db.GetRelease1Delivery(commandId, "preliminary"));
        Assert.False(db.TryRecordNonce(nonce));
        Assert.Equal(
            new[] { "install", "ack", "preliminary" },
            transport.Events);
    }

    [Fact]
    public async Task SignedChallengeWithUnknownDataFieldDoesNotRegisterOrBurnNonce()
    {
        Directory.CreateDirectory(_root);
        var dbPath = Path.Combine(_root, "malformed.db");
        var receiptPath = Path.Combine(
            _root,
            Release1ConvergenceContract.InstallReceiptFileName);
        using var pending = _deviceKeys.OpenOrCreate(Fingerprint);
        var now = DateTimeOffset.UtcNow;
        var options = new AgentOptions
        {
            AgentId = AgentId,
            MachineFingerprint = Fingerprint,
            Version = "4.0.0",
            DeviceAttestationKeyId = pending.Enrollment.KeyId,
            DeviceAttestationKeyName = pending.LocalKeyName,
            MaintenanceAttestationKeyId = MaintenanceKeyId(),
        };
        _deviceKeys.CommitPending(Fingerprint, pending.Enrollment.KeyId);
        WriteReceipt(receiptPath, options, now);
        using var db = new AgentStateDb(dbPath);
        using var deviceSigner = new DeviceAuthoritySigner(options, _deviceKeys);
        var coordinator = new Release1ConvergenceCoordinator(
            db,
            options,
            deviceSigner,
            new ChallengeTransport(),
            NullLogger.Instance,
            receiptPath,
            () => now,
            () => new string('e', 64));
        using var services = new ServiceCollection()
            .AddSingleton(db)
            .AddSingleton(coordinator)
            .AddSingleton<IDeviceAuthoritySigner>(deviceSigner)
            .BuildServiceProvider();
        var worker = new HeartbeatWorker(
            NullLogger<HeartbeatWorker>.Instance,
            Options.Create(options),
            services,
            db);
        SetVerifier(worker);
        var nonce = "66666666-6666-4666-8666-666666666666";
        var commandId = "77777777-7777-4777-8777-777777777777";
        var data = new
        {
            commandId,
            inventorySha256 = new string('8', 64),
            bridgeReleaseTag = "v4.0.0",
            bridgeSourceSha = new string('9', 40),
            expiresAt = Release1ConvergenceContract.ExactUtc(now.AddHours(1)),
            unexpected = true,
        };

        await InvokeProcessAsync(worker, SignedResponse(data, nonce, now));

        Assert.Null(db.GetRelease1Challenge(commandId));
        Assert.True(db.TryRecordNonce(nonce));
    }

    [Fact]
    public void ChallengeIsExplicitDurableMaintenanceCommand()
    {
        Assert.True(SignedCommandVerifier.IsExplicitlyClassified(
            Release1ConvergenceCommand.Name));
        Assert.Equal(
            SignedCommandAuthorityClass.DurableOutbox,
            SignedCommandVerifier.ClassifyCommand(Release1ConvergenceCommand.Name));
        Assert.Equal(
            ObservationActivationCommandClass.MaintenanceControlPlane,
            ObservationActivationCommandPolicy.Classify(
                Release1ConvergenceCommand.Name));
    }

    private JsonElement SignedResponse(object data, string nonce, DateTimeOffset now)
    {
        var dataElement = JsonSerializer.SerializeToElement(data);
        var dataHash = SignedCommandVerifier.ComputeDataHash(dataElement.GetRawText());
        var timestamp = now.ToString("O");
        var canonical = RemoteCommandTrust.BuildCommandCanonical(
            Release1ConvergenceCommand.Name,
            AgentId,
            Fingerprint,
            timestamp,
            nonce,
            dataHash);
        var signature = Convert.ToBase64String(_commandKey.SignData(
            Encoding.UTF8.GetBytes(canonical),
            HashAlgorithmName.SHA256));
        return JsonSerializer.SerializeToElement(new
        {
            data = new
            {
                signedCommand = new
                {
                    command = Release1ConvergenceCommand.Name,
                    agentId = AgentId,
                    machineFingerprint = Fingerprint,
                    timestamp,
                    nonce,
                    keyId = CommandKeyId,
                    signature,
                    data,
                },
            },
        });
    }

    private void SetVerifier(HeartbeatWorker worker)
    {
        var verifier = new SignedCommandVerifier(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [CommandKeyId] = Convert.ToBase64String(
                    _commandKey.ExportSubjectPublicKeyInfo()),
            },
            AgentId,
            Fingerprint);
        typeof(HeartbeatWorker)
            .GetField(
                "_commandVerifier",
                BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(worker, verifier);
    }

    private static async Task InvokeProcessAsync(
        HeartbeatWorker worker,
        JsonElement response)
    {
        var task = (Task)typeof(HeartbeatWorker)
            .GetMethod(
                "ProcessSignedCommandAsync",
                BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(worker, [response, CancellationToken.None])!;
        await task;
    }

    private void WriteReceipt(
        string path,
        AgentOptions options,
        DateTimeOffset now)
    {
        var receipt = new Release1InstallReceipt(
            1,
            Release1ConvergenceContract.InstallReceiptPurpose,
            Release1ConvergenceContract.HostDigest(Fingerprint),
            options.MaintenanceAttestationKeyId!,
            "v4.0.0",
            new string('5', 40),
            Release1ConvergenceContract.MsiInstallerType,
            new string('a', 64),
            new string('b', 64),
            new string('c', 64),
            new string('d', 64),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["SuavoAgent.Core.exe"] = new string('1', 64),
                ["SuavoAgent.Broker.exe"] = new string('2', 64),
                ["SuavoAgent.Helper.exe"] = new string('3', 64),
                ["SuavoAgent.Watchdog.exe"] = new string('4', 64),
                [MaintenanceContract.SignedSetupArtifactName] = new string('5', 64),
            },
            new string('6', 64),
            Release1ConvergenceContract.ExactUtc(now.AddMinutes(-1)),
            new string('d', 64),
            Release1ConvergenceContract.FullReinstallMode);
        var signature = _maintenanceKey.SignData(
            Release1ConvergenceContract.CanonicalBytes(receipt),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        File.WriteAllBytes(
            path,
            Release1ConvergenceContract.CanonicalBytes(
                new SignedRelease1InstallReceipt(
                    receipt,
                    Release1ConvergenceContract.Base64Url(signature),
                    Convert.ToBase64String(
                        _maintenanceKey.ExportSubjectPublicKeyInfo()))));
    }

    private string MaintenanceKeyId() => Convert.ToHexString(SHA256.HashData(
        _maintenanceKey.ExportSubjectPublicKeyInfo())).ToLowerInvariant();

    public void Dispose()
    {
        _commandKey.Dispose();
        _maintenanceKey.Dispose();
        _deviceKeys.Dispose();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { }
    }

    private sealed class ChallengeTransport : IRelease1ConvergenceTransport
    {
        internal List<string> Events { get; } = [];

        public Task<bool> SendInstallReceiptAsync(
            string exactRequestJson,
            CancellationToken cancellationToken)
        {
            Events.Add("install");
            return Task.FromResult(true);
        }

        public Task<bool> AckChallengeAsync(
            string commandId,
            string exactRequestJson,
            CancellationToken cancellationToken)
        {
            Events.Add("ack");
            return Task.FromResult(true);
        }

        public Task<string?> SendPreliminaryAsync(
            string exactRequestJson,
            CancellationToken cancellationToken)
        {
            Events.Add("preliminary");
            return Task.FromResult<string?>(
                "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
        }

        public Task<bool> SendFinalAsync(
            string exactRequestJson,
            CancellationToken cancellationToken)
        {
            Events.Add("final");
            return Task.FromResult(true);
        }
    }
}
