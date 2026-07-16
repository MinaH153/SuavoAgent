using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Contracts.Security;
using Xunit;

namespace SuavoAgent.Contracts.Tests.Security;

public sealed class ObservationActivationAuthorityTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "suavo-observation-activation-" + Guid.NewGuid().ToString("N"));
    private readonly ECDsa _signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly DateTimeOffset _now = new(2026, 7, 15, 15, 0, 0, TimeSpan.Zero);

    private string CurrentPath => Path.Combine(_directory, "current.json");
    private string HighWaterPath => Path.Combine(_directory, "highwater.json");
    private string ControlPath => Path.Combine(_directory, ObservationControlStateStore.FileName);
    private IReadOnlyDictionary<string, string> Keys => new Dictionary<string, string>
    {
        [RemoteCommandTrust.CommandV1KeyId] = Convert.ToBase64String(
            _signer.ExportSubjectPublicKeyInfo()),
    };

    private static ObservationActivationIdentity Identity => new(
        "11111111-1111-4111-8111-111111111111",
        "11111111-1111-4111-8111-111111111111",
        "22222222-2222-4222-8222-222222222222",
        "33333333-3333-4333-8333-333333333333",
        new string('a', 64),
        "3.92.2",
        ObservationActivationIdentityStore.PolicyDigest);

    [Fact]
    public void MissingIdentity_IsAlwaysDormant()
    {
        Directory.CreateDirectory(_directory);
        var state = SignedState(1);
        File.WriteAllText(CurrentPath, ObservationActivationAuthority.Serialize(state));
        File.WriteAllText(HighWaterPath, ObservationActivationAuthority.Serialize(state));

        var snapshot = ObservationActivationAuthority.LoadAndValidate(
            CurrentPath,
            HighWaterPath,
            identity: null,
            Keys,
            _now);

        Assert.False(snapshot.ObservationEnabled);
        Assert.Equal(ObservationActivationCodes.IdentityMissing, snapshot.Code);
    }

    [Fact]
    public void SignedBoundLease_InstallsAndEnablesObservation()
    {
        var authority = Authority();

        var installed = authority.TryInstall(SignedState(1));
        var snapshot = authority.Refresh();

        Assert.True(installed.Succeeded);
        Assert.True(snapshot.ObservationEnabled);
        Assert.Equal(1, snapshot.Generation);
        Assert.True(File.Exists(CurrentPath));
        Assert.True(File.Exists(HighWaterPath));
    }

    [Fact]
    public void SignatureOrBindingTamper_FailsClosed()
    {
        var authority = Authority();
        var state = SignedState(1);
        Assert.True(authority.TryInstall(state).Succeeded);
        var tamperedData = state.Lease.DataJson.Replace(Identity.PharmacyId,
            "99999999-9999-4999-8999-999999999999", StringComparison.Ordinal);
        var tampered = state with { Lease = state.Lease with { DataJson = tamperedData } };
        File.WriteAllText(CurrentPath, ObservationActivationAuthority.Serialize(tampered));

        var snapshot = authority.Refresh();

        Assert.False(snapshot.ObservationEnabled);
        Assert.Equal(ObservationActivationCodes.SignatureInvalid, snapshot.Code);
    }

    [Fact]
    public void ExpiredOrPrematureLease_FailsClosed()
    {
        var expired = SignedState(1, issuedAt: _now.AddMinutes(-3), expiresAt: _now.AddSeconds(-1));
        var premature = SignedState(2, issuedAt: _now, notBefore: _now.AddSeconds(20));

        Assert.False(ObservationActivationAuthority.Validate(expired, Identity, Keys, _now)
            .ObservationEnabled);
        Assert.False(ObservationActivationAuthority.Validate(premature, Identity, Keys, _now)
            .ObservationEnabled);
    }

    [Fact]
    public void ReplacingCurrentWithOlderStillLiveSignedLease_IsRejectedAcrossRestart()
    {
        var authority = Authority();
        var first = SignedState(1, leaseId: "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
        var second = SignedState(2, leaseId: "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");
        Assert.True(authority.TryInstall(first).Succeeded);
        Assert.True(authority.TryInstall(second).Succeeded);

        File.WriteAllText(CurrentPath, ObservationActivationAuthority.Serialize(first));
        var restarted = Authority();

        Assert.False(restarted.ObservationEnabled);
        Assert.Equal(ObservationActivationCodes.ReplayDetected, restarted.Snapshot.Code);
    }

    [Fact]
    public void SameGenerationDifferentLease_IsReplay()
    {
        var authority = Authority();
        Assert.True(authority.TryInstall(SignedState(
            7,
            leaseId: "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa")).Succeeded);

        var replay = authority.TryInstall(SignedState(
            7,
            leaseId: "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"));

        Assert.False(replay.Succeeded);
        Assert.Equal(ObservationActivationCodes.ReplayDetected, replay.Code);
    }

    [Fact]
    public void SameGenerationAndLeaseWithDifferentNonce_IsReplay()
    {
        var authority = Authority();
        const string leaseId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
        Assert.True(authority.TryInstall(SignedState(7, leaseId: leaseId)).Succeeded);

        var replay = authority.TryInstall(SignedState(7, leaseId: leaseId));

        Assert.False(replay.Succeeded);
        Assert.Equal(ObservationActivationCodes.ReplayDetected, replay.Code);
    }

    [Fact]
    public void ExactCommittedEnvelope_IsIdempotentButSameIdsWithDifferentBytesIsReplay()
    {
        var authority = Authority();
        const string leaseId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
        const string nonce = "55555555-5555-4555-8555-555555555555";
        var committed = SignedState(7, leaseId: leaseId, nonce: nonce);
        Assert.True(authority.TryInstall(committed).Succeeded);
        Assert.True(authority.TryInstall(committed).Succeeded);

        var changed = SignedState(
            7,
            leaseId: leaseId,
            expiresAt: _now.AddSeconds(118),
            nonce: nonce);
        var replay = authority.TryInstall(changed);

        Assert.False(replay.Succeeded);
        Assert.Equal(ObservationActivationCodes.ReplayDetected, replay.Code);
    }

    [Fact]
    public void CorruptHighWater_FailsClosed()
    {
        var authority = Authority();
        Assert.True(authority.TryInstall(SignedState(1)).Succeeded);
        File.WriteAllText(HighWaterPath, "{broken");

        var snapshot = authority.Refresh();

        Assert.False(snapshot.ObservationEnabled);
        Assert.Equal(ObservationActivationCodes.HighWaterInvalid, snapshot.Code);
    }

    [Fact]
    public void KnownGeneration_ComesOnlyFromValidSignedHighWater()
    {
        var authority = Authority();
        Assert.Equal(0, authority.GetKnownGeneration());
        Assert.True(authority.TryInstall(SignedState(9)).Succeeded);
        Assert.Equal(9, authority.GetKnownGeneration());

        File.WriteAllText(HighWaterPath, "{broken");
        Assert.Throws<InvalidDataException>(() => authority.GetKnownGeneration());
    }

    [Fact]
    public void LocalRevocation_RemovesOnlyCurrentAndCannotForgetHighWater()
    {
        var authority = Authority();
        Assert.True(authority.TryInstall(SignedState(4)).Succeeded);

        authority.RevokeLocalAuthority();

        Assert.False(authority.ObservationEnabled);
        Assert.False(File.Exists(CurrentPath));
        Assert.True(File.Exists(HighWaterPath));
        Assert.False(authority.TryInstall(SignedState(3)).Succeeded);
    }

    [Fact]
    public void CompiledPolicyDigestMatchesCanonicalPolicy()
    {
        ObservationActivationIdentityStore.AssertCompiledPolicy();
        var digest = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(ObservationActivationIdentityStore.PolicyCanonical)))
            .ToLowerInvariant();
        Assert.Equal(ObservationActivationIdentityStore.PolicyDigest, digest);
        Assert.True(ObservationActivationPolicy.AllowsApprovedPioneerRxObservation);
        Assert.False(ObservationActivationPolicy.AllowsBrowserObservation);
        Assert.False(ObservationActivationPolicy.AllowsMultiApplicationObservation);
    }

    [Fact]
    public void MissingOrCorruptControlState_IsPausedFailClosed()
    {
        var authority = new ObservationActivationAuthority(
            CurrentPath,
            HighWaterPath,
            Identity,
            Keys,
            new FixedTimeProvider(_now),
            ControlPath);
        Assert.True(authority.TryInstall(SignedState(1)).Succeeded);
        Assert.False(authority.ObservationEnabled);
        Assert.Equal(ObservationActivationCodes.ControlStateMissing, authority.Snapshot.Code);

        Directory.CreateDirectory(_directory);
        File.WriteAllText(ControlPath, "{broken");
        Assert.Equal(ObservationActivationCodes.ControlStateInvalid, authority.Refresh().Code);
    }

    [Fact]
    public void PersistedPauseStopsObservationAcrossProcessRestartUntilGenerationBoundResume()
    {
        var authority = Authority();
        Assert.True(authority.TryInstall(SignedState(1)).Succeeded);
        var running = ObservationControlStateStore.Load(ControlPath, Identity);
        Assert.False(running.Paused);
        Assert.True(ObservationControlStateStore.TryTransition(
            ControlPath,
            Identity,
            running.Generation,
            paused: true,
            stopped: false,
            _now,
            out var pauseGeneration));

        var restarted = Authority(initializeControl: false);
        Assert.False(restarted.ObservationEnabled);
        Assert.Equal(ObservationActivationCodes.ControlPaused, restarted.Snapshot.Code);
        Assert.True(ObservationControlStateStore.TryTransition(
            ControlPath,
            Identity,
            pauseGeneration,
            paused: false,
            stopped: false,
            _now.AddSeconds(1),
            out _));
        Assert.True(restarted.Refresh().ObservationEnabled);
    }

    [Fact]
    public async Task RuntimeMonitor_CancelsImmediatelyWhenAuthorityIsRevoked()
    {
        var authority = Authority();
        Assert.True(authority.TryInstall(SignedState(1)).Succeeded);
        using var lifetime = new CancellationTokenSource();
        using var monitor = new ObservationActivationRuntimeMonitor(
            authority,
            TimeSpan.FromMilliseconds(10));
        var lost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = monitor.AuthorityLostToken.Register(lost.SetResult);

        var running = monitor.RunAsync(lifetime.Token);
        authority.RevokeLocalAuthority();
        await lost.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True(monitor.AuthorityLostToken.IsCancellationRequested);
        Assert.Equal(ObservationActivationCodes.Revoked, monitor.StopCode);
        lifetime.Cancel();
        await running;
    }

    [Fact]
    public async Task RuntimeMonitor_FailsClosedWhenRefreshThrows()
    {
        using var monitor = new ObservationActivationRuntimeMonitor(
            () => throw new IOException("simulated"),
            TimeSpan.FromMilliseconds(10));

        await monitor.RunAsync(CancellationToken.None);

        Assert.True(monitor.AuthorityLostToken.IsCancellationRequested);
        Assert.Equal(ObservationActivationCodes.StateInvalid, monitor.StopCode);
    }

    private ObservationActivationAuthority Authority(bool initializeControl = true)
    {
        if (initializeControl)
            Assert.True(ObservationControlStateStore.TryInitialize(ControlPath, Identity, _now));
        return new(
            CurrentPath,
            HighWaterPath,
            Identity,
            Keys,
            new FixedTimeProvider(_now),
            ControlPath);
    }

    private ObservationActivationState SignedState(
        long generation,
        string? leaseId = null,
        DateTimeOffset? issuedAt = null,
        DateTimeOffset? notBefore = null,
        DateTimeOffset? expiresAt = null,
        string? nonce = null)
    {
        var issued = issuedAt ?? _now.AddSeconds(-1);
        var data = new ObservationActivationLeaseData(
            1,
            leaseId ?? Guid.NewGuid().ToString("D"),
            "66666666-6666-4666-8666-666666666666",
            new string('b', 64),
            Identity.PharmacyId,
            Identity.WorkstationId,
            Identity.DeviceKeyId,
            Identity.ReleaseCohort,
            generation,
            Identity.PolicyDigest,
            issued,
            notBefore ?? issued,
            expiresAt ?? issued.AddSeconds(120),
            "44444444-4444-4444-8444-444444444444");
        var dataJson = JsonSerializer.Serialize(data, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });
        var dataHash = RemoteCommandTrust.ComputeSha256Hex(dataJson);
        var timestamp = issued.ToString("O");
        nonce ??= Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var canonical = RemoteCommandTrust.BuildCommandCanonical(
            ObservationActivationAuthority.CommandName,
            Identity.AgentId,
            Identity.MachineFingerprint,
            timestamp,
            nonce,
            dataHash);
        var signature = Convert.ToBase64String(_signer.SignData(
            Encoding.UTF8.GetBytes(canonical),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        return new(1, new(
            ObservationActivationAuthority.CommandName,
            Identity.AgentId,
            Identity.MachineFingerprint,
            timestamp,
            nonce,
            RemoteCommandTrust.CommandV1KeyId,
            signature,
            dataHash,
            dataJson));
    }

    public void Dispose()
    {
        _signer.Dispose();
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
