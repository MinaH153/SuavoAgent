using System.Net;
using System.Security.Cryptography;
using System.Text;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Contracts.Security;
using SuavoAgent.Setup.Maintenance;
using Xunit;

namespace SuavoAgent.Setup.Tests.Maintenance;

public sealed class SelfUninstallCompletionFinalizerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "suavo-uninstall-finalizer-" + Guid.NewGuid().ToString("N"));
    private readonly ECDsa _cloudKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly DateTimeOffset _now = DateTimeOffset.Parse(
        "2026-07-11T21:00:00.0000000Z");
    private const string CommandKeyId = "test-command-v1";
    private const string AgentId = "11111111-1111-4111-8111-111111111111";
    private const string PharmacyId = "22222222-2222-4222-8222-222222222222";
    private const string Fingerprint = "machine-1";

    public void Dispose()
    {
        _cloudKey.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task Terminal_cleanup_persists_ticket_destroys_key_and_accepts_only_exact_receipt()
    {
        using var arranged = Arrange();
        string? postedBody = null;

        var result = await ExecuteAsync(
            arranged,
            () => TerminalCleanup(arranged.RetainedDirectory),
            (_, body, _) =>
            {
                postedBody = body;
                return Task.FromResult(ExactSuccess(body));
            });

        Assert.True(result.IsFinalized, result.Code);
        Assert.NotNull(postedBody);
        Assert.False(File.Exists(Path.Combine(
            arranged.RetainedDirectory,
            SelfUninstallCompletionContract.PendingFileName)));
        Assert.True(File.Exists(Path.Combine(
            arranged.RetainedDirectory,
            SelfUninstallCompletionContract.FinalizedFileName)));
        Assert.True(File.Exists(Path.Combine(
            arranged.RetainedDirectory,
            SelfUninstallCompletionFinalizer.CloudReceiptFileName)));
        Assert.Throws<InvalidOperationException>(() =>
            arranged.DeviceKeys.OpenExisting(Fingerprint));
        Assert.Throws<InvalidOperationException>(() =>
            arranged.MaintenanceKeys.OpenExisting(Fingerprint));
    }

    [Fact]
    public async Task Offline_post_keeps_exact_ticket_and_replay_finalizes_before_pairing()
    {
        using var arranged = Arrange();
        var postedBodies = new List<string>();
        var first = await ExecuteAsync(
            arranged,
            () => TerminalCleanup(arranged.RetainedDirectory),
            (_, body, _) =>
            {
                postedBodies.Add(body);
                return Task.FromResult(new SelfUninstallFinalizePostResult(false, "unavailable"));
            });

        Assert.False(first.IsFinalized);
        Assert.Equal("completion_response_invalid", first.Code);
        var pendingPath = Path.Combine(
            arranged.RetainedDirectory,
            SelfUninstallCompletionContract.PendingFileName);
        Assert.Equal(3, postedBodies.Count);
        Assert.Single(postedBodies.Distinct(StringComparer.Ordinal));
        var originalBody = postedBodies[0];
        Assert.True(File.Exists(pendingPath));
        Assert.Equal(originalBody, File.ReadAllText(pendingPath));
        Assert.Throws<InvalidOperationException>(() =>
            arranged.DeviceKeys.OpenExisting(Fingerprint));
        Assert.Throws<InvalidOperationException>(() =>
            arranged.MaintenanceKeys.OpenExisting(Fingerprint));

        string? replayBody = null;
        var replay = await SelfUninstallCompletionFinalizer.ReplayPendingBeforePairingAsync(
            arranged.RetentionRoot,
            arranged.DeviceKeys,
            arranged.MaintenanceKeys,
            (_, body, _) =>
            {
                replayBody = body;
                return Task.FromResult(ExactSuccess(body));
            },
            CancellationToken.None,
            (_, _) => Task.CompletedTask,
            recoveredCleanup: null,
            trustedCommandKeys: CommandKeys);

        Assert.True(replay.IsFinalized, replay.Code);
        Assert.Equal(originalBody, replayBody);
        Assert.False(File.Exists(pendingPath));
    }

    [Fact]
    public async Task BrokerAcceptedClaimRemainsValidWhenMaintenanceStartsAfterCloudExpiry()
    {
        using var arranged = Arrange();

        var result = await ExecuteAsync(
            arranged,
            () => TerminalCleanup(arranged.RetainedDirectory),
            (_, body, _) => Task.FromResult(ExactSuccess(body)),
            utcNow: _now.AddMinutes(10));

        Assert.True(result.IsFinalized, result.Code);
    }

    [Fact]
    public async Task Residue_never_creates_ticket_destroys_key_or_posts()
    {
        using var arranged = Arrange();
        var postCalls = 0;
        var cleanup = TerminalCleanup(arranged.RetainedDirectory);
        cleanup.InstallDirRemoved = false;

        var result = await ExecuteAsync(
            arranged,
            () => cleanup,
            (_, _, _) =>
            {
                postCalls++;
                return Task.FromResult(new SelfUninstallFinalizePostResult(false, ""));
            });

        Assert.False(result.IsFinalized);
        Assert.Equal("cleanup_not_terminal", result.Code);
        Assert.Equal(0, postCalls);
        Assert.False(File.Exists(Path.Combine(
            arranged.RetainedDirectory,
            SelfUninstallCompletionContract.PendingFileName)));
        using var active = arranged.DeviceKeys.OpenExisting(Fingerprint);
        Assert.Equal(arranged.DeviceKeyId, active.Enrollment.KeyId);
    }

    [Fact]
    public async Task Invalid_claim_is_rejected_before_key_or_cleanup()
    {
        using var arranged = Arrange();
        File.WriteAllText(arranged.ClaimPath, "{\"forged\":true}");
        var cleanupCalls = 0;

        var result = await ExecuteAsync(
            arranged,
            () =>
            {
                cleanupCalls++;
                return TerminalCleanup(arranged.RetainedDirectory);
            },
            (_, _, _) => throw new InvalidOperationException("must not post"));

        Assert.False(result.IsFinalized);
        Assert.Equal(0, cleanupCalls);
        using var active = arranged.DeviceKeys.OpenExisting(Fingerprint);
        Assert.Equal(arranged.DeviceKeyId, active.Enrollment.KeyId);
    }

    [Fact]
    public void Cleanup_evidence_is_derived_from_every_terminal_result_predicate()
    {
        var cleanup = TerminalCleanup(Path.Combine(_root, "retained"));
        cleanup.ProtocolRegistrationAbsent = false;

        var evidence = SelfUninstallCompletionFinalizer.CreateEvidence(cleanup, "v3.77.0+build.1");

        Assert.Equal("3.77.0", evidence.MaintenanceVersion);
        Assert.False(evidence.ProtocolRegistrationAbsent);
        Assert.Equal(1, evidence.ResidueCount);
    }

    [Fact]
    public async Task Finalization_transport_never_follows_off_origin_redirect()
    {
        using var productionHandler =
            SelfUninstallCompletionFinalizer.CreateFinalizationHandler();
        Assert.False(productionHandler.AllowAutoRedirect);
        var handler = new RedirectHandler();

        var result = await SelfUninstallCompletionFinalizer.PostForTestsAsync(
            handler,
            new Uri("https://suavollc.com"),
            "{}",
            CancellationToken.None);

        Assert.False(result.IsSuccessStatusCode);
        Assert.Equal(1, handler.Calls);
        Assert.Equal("https://suavollc.com/api/agent/self-uninstall/finalize", handler.LastUri);
    }

    [Fact]
    public async Task Incomplete_retained_claim_blocks_new_pairing_instead_of_creating_new_authority()
    {
        var retentionRoot = Path.Combine(_root, "SuavoAgent-Retained");
        var retained = Path.Combine(
            retentionRoot,
            "retained-20260711T210000000Z-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(retained);
        File.WriteAllText(
            Path.Combine(retained, SelfUninstallContract.RequestFileName + ".claimed"),
            "authenticated-claim-awaiting-recovery");
        using var keys = new InMemoryDeviceAttestationKeyProvider();
        using var maintenanceKeys = new InMemoryMaintenanceAttestationKeyProvider();

        var result = await SelfUninstallCompletionFinalizer.ReplayPendingBeforePairingAsync(
            retentionRoot,
            keys,
            maintenanceKeys,
            (_, _, _) => throw new InvalidOperationException("must not post"),
            CancellationToken.None,
            (_, _) => Task.CompletedTask);

        Assert.False(result.IsFinalized);
        Assert.Equal("incomplete_uninstall_requires_recovery", result.Code);
    }

    [Fact]
    public async Task Crash_after_cleanup_before_ticket_is_recovered_on_reinstall_replay()
    {
        using var arranged = Arrange();
        var failingKeys = new FailOnMaintenanceSignProvider(
            arranged.MaintenanceKeys,
            failOnCall: 2);
        var postCalls = 0;

        var first = await ExecuteAsync(
            arranged,
            () => TerminalCleanup(arranged.RetainedDirectory),
            (_, _, _) =>
            {
                postCalls++;
                throw new InvalidOperationException("must not post before ticket exists");
            },
            failingKeys);

        Assert.False(first.IsFinalized);
        Assert.Equal("completion_signing_failed", first.Code);
        Assert.Equal(0, postCalls);
        Assert.True(File.Exists(Path.Combine(
            arranged.RetainedDirectory,
            SelfUninstallCompletionFinalizer.RecoveryContextFileName)));
        Assert.True(File.Exists(Path.Combine(
            arranged.RetainedDirectory,
            SelfUninstallContract.RequestFileName + ".claimed")));
        Assert.False(File.Exists(Path.Combine(
            arranged.RetainedDirectory,
            SelfUninstallCompletionContract.PendingFileName)));

        var replay = await SelfUninstallCompletionFinalizer.ReplayPendingBeforePairingAsync(
            arranged.RetentionRoot,
            arranged.DeviceKeys,
            arranged.MaintenanceKeys,
            (_, body, _) => Task.FromResult(ExactSuccess(body)),
            CancellationToken.None,
            (_, _) => Task.CompletedTask,
            (_, retained) => TerminalCleanup(retained),
            CommandKeys);

        Assert.True(replay.IsFinalized, replay.Code);
        Assert.True(File.Exists(Path.Combine(
            arranged.RetainedDirectory,
            SelfUninstallCompletionContract.FinalizedFileName)));
        Assert.Throws<InvalidOperationException>(() =>
            arranged.DeviceKeys.OpenExisting(Fingerprint));
        Assert.Throws<InvalidOperationException>(() =>
            arranged.MaintenanceKeys.OpenExisting(Fingerprint));
    }

    private async Task<SelfUninstallFinalizationResult> ExecuteAsync(
        Arrangement arrangement,
        Func<ServiceInstaller.UninstallResult> uninstall,
        Func<Uri, string, CancellationToken, Task<SelfUninstallFinalizePostResult>> post,
        IMaintenanceAttestationKeyProvider? maintenanceKeys = null,
        DateTimeOffset? utcNow = null) =>
        await SelfUninstallCompletionFinalizer.ExecuteAsync(
            arrangement.ClaimPath,
            arrangement.InstallDirectory,
            arrangement.DataDirectory,
            new SelfUninstallInstalledIdentity(
                AgentId,
                PharmacyId,
                Fingerprint,
                arrangement.DeviceKeyId,
                arrangement.MaintenanceKeyId,
                new Uri("https://suavollc.com")),
            arrangement.DeviceKeys,
            maintenanceKeys ?? arrangement.MaintenanceKeys,
            () =>
            {
                var cleanup = uninstall();
                if (cleanup.RetainedDataPath is { } retained)
                    SimulateRetainedMove(arrangement.DataDirectory, retained);
                return cleanup;
            },
            post,
            CommandKeys,
            () => utcNow ?? _now,
            "3.77.0",
            CancellationToken.None,
            (_, _) => Task.CompletedTask);

    private static void SimulateRetainedMove(string dataDirectory, string retainedDirectory)
    {
        foreach (var fileName in new[]
                 {
                     SelfUninstallContract.RequestFileName + ".claimed",
                     SelfUninstallContract.RequestFileName + ".claimed" +
                         SelfUninstallAcceptanceContract.FileSuffix,
                     SelfUninstallCompletionFinalizer.RecoveryContextFileName,
                 })
        {
            var source = Path.Combine(dataDirectory, fileName);
            if (File.Exists(source))
                File.Move(source, Path.Combine(retainedDirectory, fileName), overwrite: true);
        }
    }

    private Arrangement Arrange()
    {
        var install = Path.Combine(_root, "install");
        var data = Path.Combine(_root, "data");
        var retentionRoot = Path.Combine(_root, "SuavoAgent-Retained");
        var retained = Path.Combine(
            retentionRoot,
            "retained-20260711T210000000Z-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(install);
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(retained);
        File.WriteAllText(Path.Combine(retained, "retention.json"), "{\"schemaVersion\":1}");
        var claimPath = Path.Combine(
            data,
            SelfUninstallContract.RequestFileName + ".claimed");
        var request = Request();
        var exactClaim = SelfUninstallContract.Serialize(request);
        File.WriteAllText(claimPath, exactClaim);

        var keys = new InMemoryDeviceAttestationKeyProvider();
        using var pending = keys.OpenOrCreate(Fingerprint);
        var keyId = pending.Enrollment.KeyId;
        keys.CommitPending(Fingerprint, keyId);
        var maintenanceKeys = new InMemoryMaintenanceAttestationKeyProvider();
        var maintenance = maintenanceKeys.OpenOrCreate(Fingerprint);
        var unsignedAcceptance = new SelfUninstallBrokerAcceptance(
            SelfUninstallAcceptanceContract.SchemaVersion,
            request.CommandId,
            request.Nonce,
            request.AgentId,
            request.MachineFingerprint,
            RemoteCommandTrust.ComputeSha256Hex(exactClaim),
            SelfUninstallAcceptanceContract.FormatTimestamp(_now),
            _now.AddMinutes(4).ToString("O"),
            maintenance.Enrollment.KeyId,
            maintenance.Enrollment.PublicKeySpki,
            string.Empty);
        var acceptedSignature = maintenanceKeys.Sign(
            Fingerprint,
            maintenance.Enrollment.KeyId,
            Encoding.UTF8.GetBytes(
                SelfUninstallAcceptanceContract.BuildCanonical(unsignedAcceptance)));
        File.WriteAllText(
            SelfUninstallAcceptanceContract.PathForClaim(claimPath),
            SelfUninstallAcceptanceContract.Serialize(unsignedAcceptance with
            {
                Signature = SelfUninstallAcceptanceContract.Base64UrlEncode(
                    acceptedSignature.Signature.Span),
            }));
        return new(
            install,
            data,
            retentionRoot,
            retained,
            claimPath,
            keys,
            keyId,
            maintenanceKeys,
            maintenance.Enrollment.KeyId);
    }

    private ServiceInstaller.UninstallResult TerminalCleanup(string retainedDirectory) => new()
    {
        ServicesRemoved = true,
        ServicesRemaining = 0,
        DataDirRemoved = true,
        DataPreserved = true,
        RetainedDataPath = retainedDirectory,
        InstallDirRemoved = true,
        ScheduledUninstallTaskAbsent = true,
        ProtocolRegistrationAbsent = true,
        ArpRegistrationAbsent = true,
        RetainedEvidencePresent = true,
        OperationalCredentialsAbsent = true,
    };

    private SelfUninstallFinalizePostResult ExactSuccess(string body)
    {
        Assert.True(SelfUninstallCompletionContract.TryDeserialize(
            body, out var envelope, out var code), code);
        var digest = SelfUninstallCompletionContract.ComputeReceiptDigest(envelope!.Ticket);
        return new(
            true,
            $$"""{"status":"finalized","commandId":"33333333-3333-4333-8333-333333333333","receiptDigest":"{{digest}}"}""");
    }

    private SelfUninstallRequest Request()
    {
        var timestamp = _now.ToString("O");
        const string commandId = "33333333-3333-4333-8333-333333333333";
        const string nonce = "44444444-4444-4444-8444-444444444444";
        var dataJson =
            $"{{\"commandId\":\"33333333-3333-4333-8333-333333333333\",\"expiresAt\":\"{_now.AddMinutes(4):O}\"}}";
        var dataHash = RemoteCommandTrust.ComputeSha256Hex(dataJson);
        var signature = Sign(RemoteCommandTrust.BuildCommandCanonical(
            SelfUninstallContract.CommandName,
            AgentId,
            Fingerprint,
            timestamp,
            nonce,
            dataHash));
        var archiveDigest = RemoteCommandTrust.ComputeSha256Hex("archive");
        var receipt = new SelfUninstallArchiveReceipt(
            "55555555-5555-4555-8555-555555555555",
            archiveDigest,
            _now.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
            nonce,
            CommandKeyId,
            string.Empty);
        receipt = receipt with
        {
            Signature = Sign(RemoteCommandTrust.BuildArchiveReceiptCanonical(
                receipt,
                AgentId,
                Fingerprint,
                commandId,
                nonce)),
        };
        return new(
            SelfUninstallContract.SchemaVersion,
            SelfUninstallContract.CommandName,
            AgentId,
            Fingerprint,
            timestamp,
            nonce,
            CommandKeyId,
            signature,
            dataJson,
            dataHash,
            commandId,
            timestamp,
            archiveDigest,
            receipt);
    }

    private IReadOnlyDictionary<string, string> CommandKeys =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [CommandKeyId] = Convert.ToBase64String(_cloudKey.ExportSubjectPublicKeyInfo()),
        };

    private string Sign(string canonical) => Convert.ToBase64String(
        _cloudKey.SignData(Encoding.UTF8.GetBytes(canonical), HashAlgorithmName.SHA256));

    private sealed record Arrangement(
        string InstallDirectory,
        string DataDirectory,
        string RetentionRoot,
        string RetainedDirectory,
        string ClaimPath,
        InMemoryDeviceAttestationKeyProvider DeviceKeys,
        string DeviceKeyId,
        InMemoryMaintenanceAttestationKeyProvider MaintenanceKeys,
        string MaintenanceKeyId) : IDisposable
    {
        public void Dispose()
        {
            DeviceKeys.Dispose();
            MaintenanceKeys.Dispose();
        }
    }

    private sealed class FailOnMaintenanceSignProvider(
        IMaintenanceAttestationKeyProvider inner,
        int failOnCall) : IMaintenanceAttestationKeyProvider
    {
        private int _calls;

        public MaintenanceKeyRegistration OpenOrCreate(string fingerprint) =>
            inner.OpenOrCreate(fingerprint);

        public MaintenanceKeyRegistration OpenExisting(string fingerprint) =>
            inner.OpenExisting(fingerprint);

        public DeviceMaintenanceSignature Sign(
            string fingerprint,
            string keyId,
            ReadOnlyMemory<byte> bytes)
        {
            if (Interlocked.Increment(ref _calls) == failOnCall)
                throw new CryptographicException("simulated crash boundary");
            return inner.Sign(fingerprint, keyId, bytes);
        }

        public void DestroyForUninstall(string fingerprint, string keyId) =>
            inner.DestroyForUninstall(fingerprint, keyId);
    }

    private sealed class RedirectHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? LastUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            LastUri = request.RequestUri?.AbsoluteUri;
            var response = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect)
            {
                Content = new StringContent("redirect rejected"),
            };
            response.Headers.Location = new Uri("https://evil.example/steal-ticket");
            return Task.FromResult(response);
        }
    }
}
