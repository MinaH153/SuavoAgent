using System.Globalization;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Health;
using SuavoAgent.Core.State;

namespace SuavoAgent.Core.Cloud;

internal sealed class Release1ConvergenceCoordinator
{
    private readonly AgentStateDb _stateDb;
    private readonly AgentOptions _options;
    private readonly IDeviceAuthoritySigner _deviceSigner;
    private readonly IRelease1ConvergenceTransport _transport;
    private readonly ILogger _logger;
    private readonly string _installReceiptPath;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<string> _currentBootId;
    private readonly IReadOnlyDictionary<string, string> _otaRoots;

    internal Release1ConvergenceCoordinator(
        AgentStateDb stateDb,
        AgentOptions options,
        IDeviceAuthoritySigner deviceSigner,
        IRelease1ConvergenceTransport transport,
        ILogger logger,
        string? installReceiptPath = null,
        Func<DateTimeOffset>? utcNow = null,
        Func<string>? currentBootId = null,
        IReadOnlyDictionary<string, string>? otaRoots = null)
    {
        _stateDb = stateDb ?? throw new ArgumentNullException(nameof(stateDb));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _deviceSigner = deviceSigner ??
            throw new ArgumentNullException(nameof(deviceSigner));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        if (string.IsNullOrWhiteSpace(options.MachineFingerprint) ||
            string.IsNullOrWhiteSpace(options.AgentId) ||
            string.IsNullOrWhiteSpace(options.MaintenanceAttestationKeyId) ||
            string.IsNullOrWhiteSpace(options.Version) ||
            !string.Equals(
                options.DeviceAttestationKeyId,
                deviceSigner.KeyId,
                StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Release 1 convergence device identity is incomplete.");
        _installReceiptPath = installReceiptPath ?? Path.Combine(
            RuntimeHealthEvidence.ProgramDataRoot,
            Release1ConvergenceContract.InstallReceiptFileName);
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _currentBootId = currentBootId ?? (() =>
            Release1ConvergenceContract.CurrentBootId(
                options.MachineFingerprint));
        _otaRoots = new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(
                otaRoots ?? OtaUpdateTrust.ProductionTrustedPublicKeys,
                StringComparer.Ordinal));
    }

    internal async Task<bool> RegisterAndRetryAsync(
        Release1ConvergenceChallenge challenge,
        CancellationToken cancellationToken)
    {
        Release1ConvergenceRegistration registration;
        try
        {
            registration = _stateDb.RegisterRelease1Challenge(
                challenge,
                _utcNow());
        }
        catch (Exception exception) when (exception is
            InvalidOperationException or ArgumentException or SqliteException)
        {
            _logger.LogWarning(
                "release1.convergence.challenge_rejected exception_type={ExceptionType}",
                exception.GetType().Name);
            return false;
        }
        if (!registration.Accepted)
        {
            _logger.LogWarning(
                "release1.convergence.challenge_rejected code={Code}",
                registration.Code);
            return false;
        }

        await TryRetryChallengeAsync(challenge, cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    internal async Task RetryPendingAsync(CancellationToken cancellationToken)
    {
        await TryUploadInstallReceiptAsync(cancellationToken)
            .ConfigureAwait(false);
        var pending = _stateDb.GetPendingRelease1Challenges(_utcNow());
        foreach (var challenge in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await TryRetryChallengeAsync(challenge, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task TryRetryChallengeAsync(
        Release1ConvergenceChallenge challenge,
        CancellationToken cancellationToken)
    {
        try
        {
            await RetryChallengeAsync(challenge, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or InvalidDataException or
            InvalidOperationException or CryptographicException or HttpRequestException or
            FormatException or JsonException or SqliteException)
        {
            _logger.LogWarning(
                "release1.convergence.retry_pending exception_type={ExceptionType}",
                exception.GetType().Name);
        }
    }

    private async Task RetryChallengeAsync(
        Release1ConvergenceChallenge challenge,
        CancellationToken cancellationToken)
    {
        var now = _utcNow().ToUniversalTime();
        var expiresAt = Release1InstallReceiptVerifier.ParseExactUtc(
            challenge.ExpiresAtUtc,
            "challenge expiry time");
        if (now > expiresAt) return;

        var uploaded = await EnsureInstallReceiptUploadedAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!uploaded) return;

        // Verify the maintenance-signed install and the crossed Windows boot
        // before telling the control plane the challenge executed.
        _ = LoadActivationEvidence(challenge, now);
        var ackJson = CanonicalJson(new Release1ChallengeAckRequest(
            "executed",
            new(challenge.CommandId, challenge.InventorySha256)));
        var ackSha256 = Sha256(ackJson);
        if (_stateDb.GetRelease1Delivery(
                challenge.CommandId,
                "challenge_ack") is null)
        {
            if (!await _transport.AckChallengeAsync(
                    challenge.CommandId,
                    ackJson,
                    cancellationToken).ConfigureAwait(false))
                return;
            _stateDb.RecordRelease1Delivery(
                challenge.CommandId,
                "challenge_ack",
                ackSha256,
                responseCommandId: null,
                _utcNow());
        }

        var preliminary = _stateDb.GetRelease1Preliminary(challenge.CommandId) ??
            CreatePreliminary(challenge);
        ValidatePreliminary(challenge, preliminary);
        if (_stateDb.GetRelease1Delivery(
                challenge.CommandId,
                "preliminary") is null)
        {
            var updateCommandId = await _transport.SendPreliminaryAsync(
                    preliminary.RequestJson,
                    cancellationToken)
                .ConfigureAwait(false);
            if (updateCommandId is null) return;
            _stateDb.RecordRelease1Delivery(
                challenge.CommandId,
                "preliminary",
                preliminary.RequestSha256,
                updateCommandId,
                _utcNow());
        }

        var final = _stateDb.GetRelease1Final(challenge.CommandId);
        if (final is null)
        {
            var preliminaryDelivery = _stateDb.GetRelease1Delivery(
                challenge.CommandId,
                "preliminary");
            if (preliminaryDelivery?.ResponseCommandId is null)
                throw new InvalidDataException(
                    "Release 1 preliminary update command binding is missing.");
            var noop = _stateDb.GetReleaseNoopDeviceReceipt(
                preliminaryDelivery.ResponseCommandId);
            if (noop is null) return;
            final = CreateFinal(challenge, preliminary, noop);
        }
        ValidateFinal(challenge, preliminary, final);
        if (_stateDb.GetRelease1Delivery(challenge.CommandId, "final") is not null)
            return;
        if (await _transport.SendFinalAsync(
                final.RequestJson,
                cancellationToken).ConfigureAwait(false))
        {
            _stateDb.RecordRelease1Delivery(
                challenge.CommandId,
                "final",
                final.RequestSha256,
                responseCommandId: null,
                _utcNow());
        }
    }

    private async Task TryUploadInstallReceiptAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_installReceiptPath))
            return;
        try
        {
            _ = await EnsureInstallReceiptUploadedAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or InvalidDataException or
            InvalidOperationException or CryptographicException or HttpRequestException or
            FormatException or JsonException or SqliteException)
        {
            _logger.LogWarning(
                "release1.install_receipt.retry_pending exception_type={ExceptionType}",
                exception.GetType().Name);
        }
    }

    private async Task<bool> EnsureInstallReceiptUploadedAsync(
        CancellationToken cancellationToken)
    {
        var verified = Release1InstallReceiptVerifier.ReadAndVerifyLocal(
            _installReceiptPath,
            _options,
            _utcNow());
        var upload = _stateDb.GetOrCreateRelease1InstallUpload(
            verified.Envelope,
            verified.InstallReceiptSha256);
        if (_stateDb.HasRelease1InstallDelivery(upload.InstallReceiptSha256))
            return true;
        if (!await _transport.SendInstallReceiptAsync(
                upload.RequestJson,
                cancellationToken).ConfigureAwait(false))
            return false;
        _stateDb.RecordRelease1InstallDelivery(upload, _utcNow());
        return true;
    }

    private PersistedRelease1Preliminary CreatePreliminary(
        Release1ConvergenceChallenge challenge)
    {
        var now = _utcNow().ToUniversalTime();
        var activation = LoadActivationEvidence(challenge, now);
        var restart = new Release1RestartReceipt(
            SchemaVersion: Release1ConvergenceContract.ReceiptSchemaVersion,
            Purpose: Release1ConvergenceContract.RestartReceiptPurpose,
            HostDigest: Release1ConvergenceContract.HostDigest(
                _options.MachineFingerprint!),
            InstallReceiptSha256: activation.Install.InstallReceiptSha256,
            BootIdBeforeRestart: activation.Install.Envelope.InstallReceipt.BootIdAtInstall,
            BootIdAfterRestart: activation.CurrentBootId,
            RunningReleaseTag: challenge.BridgeReleaseTag,
            RunningSourceSha: challenge.BridgeSourceSha,
            Outcome: Release1ConvergenceContract.RestartOutcome,
            RestartObservedAtUtc: Release1ConvergenceContract.ExactUtc(now));
        var restartSha256 = Release1ConvergenceContract.CanonicalSha256(restart);
        var proof = new Release1PreliminaryConvergenceProof(
            SchemaVersion: Release1ConvergenceContract.PreliminaryProofSchemaVersion,
            Purpose: Release1ConvergenceContract.PreliminaryProofPurpose,
            AttestationAuthority: Release1ConvergenceContract.AttestationAuthority,
            AttestationKeyId: _deviceSigner.KeyId,
            HostDigest: restart.HostDigest,
            InventorySha256: challenge.InventorySha256,
            InstallReceipt: activation.Install.Envelope.InstallReceipt,
            InstallReceiptSha256: activation.Install.InstallReceiptSha256,
            InstallReceiptSignatureBase64Url:
                activation.Install.Envelope.InstallReceiptSignatureBase64Url,
            RestartReceipt: restart,
            RestartReceiptSha256: restartSha256,
            VerifiedAtUtc: Release1ConvergenceContract.ExactUtc(now),
            PhiClassification: Release1ConvergenceContract.PhiClassification);
        var signed = _deviceSigner.SignRelease1Preliminary(proof);
        var request = new Release1PreliminaryRequest(
            signed.Proof,
            signed.ProofSignatureBase64Url);
        ValidatePreliminary(
            challenge,
            new PersistedRelease1Preliminary(
                challenge.CommandId,
                string.Empty,
                string.Empty,
                request,
                proof.InstallReceiptSha256,
                proof.RestartReceiptSha256,
                proof.VerifiedAtUtc));
        return _stateDb.GetOrCreateRelease1Preliminary(
            challenge.CommandId,
            request);
    }

    private PersistedRelease1Final CreateFinal(
        Release1ConvergenceChallenge challenge,
        PersistedRelease1Preliminary preliminary,
        AgentStateDb.PersistedReleaseNoopDeviceReceipt noop)
    {
        var raw = noop.Signed.Receipt;
        ValidateRawNoop(challenge, preliminary, raw);
        var observedAt = DateTimeOffset.Parse(
            raw.VerifiedAtUtc,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind).ToUniversalTime();
        var observedAtExact = Release1ConvergenceContract.ExactUtc(observedAt);
        var noopReceipt = new Release1V1NoopRehearsalReceipt(
            SchemaVersion: Release1ConvergenceContract.ReceiptSchemaVersion,
            Purpose: Release1ConvergenceContract.V1NoopReceiptPurpose,
            HostDigest: preliminary.Request.Proof.HostDigest,
            InventorySha256: challenge.InventorySha256,
            InstallReceiptSha256: preliminary.InstallReceiptSha256,
            RestartReceiptSha256: preliminary.RestartReceiptSha256,
            InstalledReleaseTag: challenge.BridgeReleaseTag,
            InstalledSourceSha: challenge.BridgeSourceSha,
            OtaSigningKeyId: raw.OtaSigningKeyId,
            UpdateManifestName: raw.ManifestName!,
            UpdateManifestCanonical: raw.ManifestCanonical,
            UpdateManifestSignatureP1363Hex: raw.ManifestSignature,
            ChecksumsSha256: raw.ChecksumsSha256!,
            ChecksumsSignatureSha256: raw.ChecksumsSignatureSha256!,
            Outcome: Release1ConvergenceContract.V1NoopOutcome,
            ObservedAtUtc: observedAtExact);
        var noopReceiptSha256 =
            Release1ConvergenceContract.CanonicalSha256(noopReceipt);
        var now = _utcNow().ToUniversalTime();
        if (now < observedAt) now = observedAt;
        var expiresAt = Release1InstallReceiptVerifier.ParseExactUtc(
            challenge.ExpiresAtUtc,
            "challenge expiry time");
        if (now > expiresAt)
            throw new InvalidDataException(
                "Release 1 convergence authority expired before final proof.");
        var proof = preliminary.Request.Proof;
        var attestation = new Release1DeviceConvergenceAttestation(
            SchemaVersion: Release1ConvergenceContract.DeviceAttestationSchemaVersion,
            Purpose: Release1ConvergenceContract.AttestationPurpose,
            AttestationAuthority: Release1ConvergenceContract.AttestationAuthority,
            AttestationKeyId: _deviceSigner.KeyId,
            HostDigest: proof.HostDigest,
            InventorySha256: challenge.InventorySha256,
            InstallReceipt: proof.InstallReceipt,
            InstallReceiptSha256: proof.InstallReceiptSha256,
            RestartReceipt: proof.RestartReceipt,
            RestartReceiptSha256: proof.RestartReceiptSha256,
            V1NoopRehearsalReceipt: noopReceipt,
            V1NoopRehearsalReceiptSha256: noopReceiptSha256,
            VerifiedAtUtc: Release1ConvergenceContract.ExactUtc(now),
            PhiClassification: Release1ConvergenceContract.PhiClassification);
        var request = new Release1FinalRequest(
            attestation,
            _deviceSigner.SignRelease1Attestation(attestation),
            proof.InstallReceiptSignatureBase64Url);
        ValidateFinal(
            challenge,
            preliminary,
            new PersistedRelease1Final(
                challenge.CommandId,
                raw.CommandId,
                string.Empty,
                string.Empty,
                request,
                attestation.VerifiedAtUtc));
        return _stateDb.GetOrCreateRelease1Final(
            challenge.CommandId,
            raw.CommandId,
            request);
    }

    private ActivationEvidence LoadActivationEvidence(
        Release1ConvergenceChallenge challenge,
        DateTimeOffset now)
    {
        var install = Release1InstallReceiptVerifier.ReadAndVerify(
            _installReceiptPath,
            challenge,
            _options,
            now);
        var currentBootId = _currentBootId();
        if (!Release1InstallReceiptVerifier.IsLowerHex(currentBootId, 64) ||
            FixedTextEquals(
                install.Envelope.InstallReceipt.BootIdAtInstall,
                currentBootId))
            throw new InvalidDataException(
                "Release 1 convergence requires a completed post-install Windows restart.");
        return new(install, currentBootId);
    }

    private void ValidatePreliminary(
        Release1ConvergenceChallenge challenge,
        PersistedRelease1Preliminary preliminary)
    {
        var proof = preliminary.Request.Proof;
        var restart = proof.RestartReceipt;
        var install = proof.InstallReceipt;
        var installCompleted = Release1InstallReceiptVerifier.ParseExactUtc(
            install.InstallCompletedAtUtc,
            "install completion time");
        var restartObserved = Release1InstallReceiptVerifier.ParseExactUtc(
            restart.RestartObservedAtUtc,
            "restart observation time");
        var verifiedAt = Release1InstallReceiptVerifier.ParseExactUtc(
            proof.VerifiedAtUtc,
            "preliminary verification time");
        var expiresAt = Release1InstallReceiptVerifier.ParseExactUtc(
            challenge.ExpiresAtUtc,
            "challenge expiry time");
        if (proof.SchemaVersion !=
                Release1ConvergenceContract.PreliminaryProofSchemaVersion ||
            !string.Equals(
                proof.Purpose,
                Release1ConvergenceContract.PreliminaryProofPurpose,
                StringComparison.Ordinal) ||
            !string.Equals(
                proof.AttestationAuthority,
                Release1ConvergenceContract.AttestationAuthority,
                StringComparison.Ordinal) ||
            !FixedTextEquals(proof.AttestationKeyId, _deviceSigner.KeyId) ||
            !FixedTextEquals(
                proof.HostDigest,
                Release1ConvergenceContract.HostDigest(
                    _options.MachineFingerprint!)) ||
            !FixedTextEquals(
                proof.InventorySha256,
                challenge.InventorySha256) ||
            !FixedTextEquals(
                proof.InstallReceiptSha256,
                Release1ConvergenceContract.CanonicalSha256(install)) ||
            !FixedTextEquals(
                proof.RestartReceiptSha256,
                Release1ConvergenceContract.CanonicalSha256(restart)) ||
            !string.Equals(
                proof.PhiClassification,
                Release1ConvergenceContract.PhiClassification,
                StringComparison.Ordinal) ||
            !string.Equals(
                restart.Purpose,
                Release1ConvergenceContract.RestartReceiptPurpose,
                StringComparison.Ordinal) ||
            restart.SchemaVersion != Release1ConvergenceContract.ReceiptSchemaVersion ||
            !FixedTextEquals(restart.HostDigest, proof.HostDigest) ||
            !FixedTextEquals(
                restart.InstallReceiptSha256,
                proof.InstallReceiptSha256) ||
            !FixedTextEquals(
                restart.BootIdBeforeRestart,
                install.BootIdAtInstall) ||
            FixedTextEquals(
                restart.BootIdBeforeRestart,
                restart.BootIdAfterRestart) ||
            !string.Equals(
                restart.RunningReleaseTag,
                challenge.BridgeReleaseTag,
                StringComparison.Ordinal) ||
            !FixedTextEquals(
                restart.RunningSourceSha,
                challenge.BridgeSourceSha) ||
            !string.Equals(
                restart.Outcome,
                Release1ConvergenceContract.RestartOutcome,
                StringComparison.Ordinal) ||
            restartObserved < installCompleted ||
            verifiedAt < restartObserved ||
            verifiedAt > expiresAt)
            throw new InvalidDataException(
                "Release 1 preliminary proof binding is invalid.");
        using var signature = new SensitiveBytes(
            Release1InstallReceiptVerifier.DecodeExactBase64UrlP1363(
                preliminary.Request.ProofSignatureBase64Url));
    }

    private void ValidateRawNoop(
        Release1ConvergenceChallenge challenge,
        PersistedRelease1Preliminary preliminary,
        ReleaseNoopDeviceReceipt raw)
    {
        var restartObserved = Release1InstallReceiptVerifier.ParseExactUtc(
            preliminary.Request.Proof.RestartReceipt.RestartObservedAtUtc,
            "restart observation time");
        if (!DateTimeOffset.TryParse(
                raw.VerifiedAtUtc,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var observedAt))
            throw new InvalidDataException("Release 1 no-op time is invalid.");
        observedAt = observedAt.ToUniversalTime();
        var expiresAt = Release1InstallReceiptVerifier.ParseExactUtc(
            challenge.ExpiresAtUtc,
            "challenge expiry time");
        if (!_otaRoots.TryGetValue(
                OtaUpdateTrust.LegacyV1KeyId,
                out var v1PublicKey))
            throw new InvalidDataException("Release 1 historic OTA root is unavailable.");
        var v1Root = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [OtaUpdateTrust.LegacyV1KeyId] = v1PublicKey,
        };
        if (!string.Equals(
                raw.OtaSigningKeyId,
                OtaUpdateTrust.LegacyV1KeyId,
                StringComparison.Ordinal) ||
            !string.Equals(raw.ReleaseTag, challenge.BridgeReleaseTag, StringComparison.Ordinal) ||
            !FixedTextEquals(raw.SourceSha, challenge.BridgeSourceSha) ||
            !FixedTextEquals(raw.InventorySha256, challenge.InventorySha256) ||
            !FixedTextEquals(
                raw.InstallReceiptSha256,
                preliminary.InstallReceiptSha256) ||
            !FixedTextEquals(
                raw.RestartReceiptSha256,
                preliminary.RestartReceiptSha256) ||
            !FixedTextEquals(
                raw.ChecksumsSha256,
                preliminary.Request.Proof.InstallReceipt.ChecksumsSha256) ||
            !FixedTextEquals(
                raw.ChecksumsSignatureSha256,
                preliminary.Request.Proof.InstallReceipt.ChecksumsSignatureSha256) ||
            !string.Equals(
                raw.ManifestName,
                $"update-manifest-{challenge.BridgeReleaseTag}.txt",
                StringComparison.Ordinal) ||
            !UpdateActivationContract.VersionsEquivalent(
                raw.TargetVersion,
                challenge.BridgeReleaseTag) ||
            !raw.ManifestCanonical.All(char.IsAscii) ||
            !OtaUpdateTrust.VerifyP1363Hex(
                v1Root,
                raw.ManifestCanonical,
                raw.ManifestSignature) ||
            observedAt < restartObserved ||
            observedAt > expiresAt)
            throw new InvalidDataException(
                "Release 1 v1 no-op receipt binding is invalid.");
    }

    private void ValidateFinal(
        Release1ConvergenceChallenge challenge,
        PersistedRelease1Preliminary preliminary,
        PersistedRelease1Final final)
    {
        var attestation = final.Request.Attestation;
        var noop = attestation.V1NoopRehearsalReceipt;
        var noopObserved = Release1InstallReceiptVerifier.ParseExactUtc(
            noop.ObservedAtUtc,
            "v1 no-op observation time");
        var verifiedAt = Release1InstallReceiptVerifier.ParseExactUtc(
            attestation.VerifiedAtUtc,
            "final verification time");
        var expiresAt = Release1InstallReceiptVerifier.ParseExactUtc(
            challenge.ExpiresAtUtc,
            "challenge expiry time");
        if (attestation.SchemaVersion !=
                Release1ConvergenceContract.DeviceAttestationSchemaVersion ||
            !string.Equals(
                attestation.Purpose,
                Release1ConvergenceContract.AttestationPurpose,
                StringComparison.Ordinal) ||
            !string.Equals(
                attestation.AttestationAuthority,
                Release1ConvergenceContract.AttestationAuthority,
                StringComparison.Ordinal) ||
            !FixedTextEquals(attestation.AttestationKeyId, _deviceSigner.KeyId) ||
            !FixedTextEquals(
                attestation.HostDigest,
                preliminary.Request.Proof.HostDigest) ||
            !FixedTextEquals(
                attestation.InventorySha256,
                challenge.InventorySha256) ||
            attestation.InstallReceipt != preliminary.Request.Proof.InstallReceipt ||
            !FixedTextEquals(
                attestation.InstallReceiptSha256,
                preliminary.InstallReceiptSha256) ||
            attestation.RestartReceipt != preliminary.Request.Proof.RestartReceipt ||
            !FixedTextEquals(
                attestation.RestartReceiptSha256,
                preliminary.RestartReceiptSha256) ||
            !FixedTextEquals(
                attestation.V1NoopRehearsalReceiptSha256,
                Release1ConvergenceContract.CanonicalSha256(noop)) ||
            !string.Equals(
                attestation.PhiClassification,
                Release1ConvergenceContract.PhiClassification,
                StringComparison.Ordinal) ||
            !FixedTextEquals(
                final.Request.InstallReceiptSignatureBase64Url,
                preliminary.Request.Proof.InstallReceiptSignatureBase64Url) ||
            verifiedAt < noopObserved ||
            verifiedAt > expiresAt)
            throw new InvalidDataException(
                "Release 1 final evidence binding is invalid.");
        using var attestationSignature = new SensitiveBytes(
            Release1InstallReceiptVerifier.DecodeExactBase64UrlP1363(
                final.Request.AttestationSignatureBase64Url));
        using var installSignature = new SensitiveBytes(
            Release1InstallReceiptVerifier.DecodeExactBase64UrlP1363(
                final.Request.InstallReceiptSignatureBase64Url));
    }

    private static string CanonicalJson<T>(T value) =>
        Encoding.UTF8.GetString(
            Release1ConvergenceContract.CanonicalBytes(value));

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private static bool FixedTextEquals(string? left, string? right) =>
        left is not null && right is not null && left.Length == right.Length &&
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(left),
            Encoding.UTF8.GetBytes(right));

    private sealed record ActivationEvidence(
        VerifiedRelease1InstallReceipt Install,
        string CurrentBootId);

    private sealed class SensitiveBytes(byte[] value) : IDisposable
    {
        public void Dispose() => CryptographicOperations.ZeroMemory(value);
    }
}
