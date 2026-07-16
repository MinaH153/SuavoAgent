using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Core.Cloud;

namespace SuavoAgent.Core.State;

internal sealed record Release1ConvergenceRegistration(
    bool Accepted,
    bool IsReplay,
    string Code);

internal sealed record PersistedRelease1Preliminary(
    string CommandId,
    string RequestJson,
    string RequestSha256,
    Release1PreliminaryRequest Request,
    string InstallReceiptSha256,
    string RestartReceiptSha256,
    string VerifiedAtUtc);

internal sealed record PersistedRelease1Final(
    string CommandId,
    string NoopCommandId,
    string RequestJson,
    string RequestSha256,
    Release1FinalRequest Request,
    string VerifiedAtUtc);

internal sealed record Release1ConvergenceDelivery(
    string CommandId,
    string Phase,
    string RequestSha256,
    string? ResponseCommandId,
    string AcceptedAtUtc);

public sealed partial class AgentStateDb
{
    private const int MaxRelease1RequestBytes = 256 * 1024;
    private static readonly TimeSpan MaximumRelease1ChallengeLifetime =
        TimeSpan.FromDays(7);

    internal Release1ConvergenceRegistration RegisterRelease1Challenge(
        Release1ConvergenceChallenge challenge,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        ValidateRelease1Challenge(challenge, now);

        lock (_connLock)
        {
            using var transaction = _conn.BeginTransaction();
            var existing = ReadRelease1Challenge(
                transaction,
                challenge.CommandId,
                challenge.Envelope.Nonce);
            if (existing is not null)
            {
                transaction.Commit();
                return existing == challenge
                    ? new(true, true, "release1_challenge_exact_replay")
                    : new(false, false, "release1_challenge_binding_conflict");
            }

            using var insert = CreateCommand(transaction, """
                INSERT INTO release1_convergence_challenges (
                    command_id, envelope_nonce, command_name, agent_id,
                    machine_fingerprint, command_timestamp, command_data_hash,
                    command_key_id, command_signature, inventory_sha256,
                    bridge_release_tag, bridge_source_sha, expires_at_utc,
                    registered_at_utc
                ) VALUES (
                    @commandId, @nonce, @command, @agentId,
                    @fingerprint, @timestamp, @dataHash,
                    @keyId, @signature, @inventorySha256,
                    @releaseTag, @sourceSha, @expiresAt, @registeredAt
                )
                """);
            insert.Parameters.AddWithValue("@commandId", challenge.CommandId);
            insert.Parameters.AddWithValue("@nonce", challenge.Envelope.Nonce);
            insert.Parameters.AddWithValue("@command", challenge.Envelope.Command);
            insert.Parameters.AddWithValue("@agentId", challenge.Envelope.AgentId);
            insert.Parameters.AddWithValue(
                "@fingerprint",
                challenge.Envelope.MachineFingerprint);
            insert.Parameters.AddWithValue("@timestamp", challenge.Envelope.Timestamp);
            insert.Parameters.AddWithValue("@dataHash", challenge.Envelope.DataHash);
            insert.Parameters.AddWithValue("@keyId", challenge.Envelope.KeyId);
            insert.Parameters.AddWithValue("@signature", challenge.Envelope.Signature);
            insert.Parameters.AddWithValue(
                "@inventorySha256",
                challenge.InventorySha256);
            insert.Parameters.AddWithValue("@releaseTag", challenge.BridgeReleaseTag);
            insert.Parameters.AddWithValue("@sourceSha", challenge.BridgeSourceSha);
            insert.Parameters.AddWithValue("@expiresAt", challenge.ExpiresAtUtc);
            insert.Parameters.AddWithValue(
                "@registeredAt",
                Release1ConvergenceContract.ExactUtc(now));
            insert.ExecuteNonQuery();
            transaction.Commit();
            return new(true, false, "release1_challenge_registered");
        }
    }

    internal IReadOnlyList<Release1ConvergenceChallenge>
        GetPendingRelease1Challenges(DateTimeOffset now)
    {
        lock (_connLock)
        {
            using var command = _conn.CreateCommand();
            command.CommandText = """
                SELECT command_id, envelope_nonce, command_name, agent_id,
                       machine_fingerprint, command_timestamp, command_data_hash,
                       command_key_id, command_signature, inventory_sha256,
                       bridge_release_tag, bridge_source_sha, expires_at_utc
                  FROM release1_convergence_challenges challenge
                 WHERE NOT EXISTS (
                       SELECT 1 FROM release1_convergence_deliveries delivery
                        WHERE delivery.command_id = challenge.command_id
                          AND delivery.phase = 'final')
                 ORDER BY registered_at_utc, command_id
                """;
            using var reader = command.ExecuteReader();
            var pending = new List<Release1ConvergenceChallenge>();
            while (reader.Read())
            {
                var challenge = MaterializeRelease1Challenge(reader);
                if (ParseRelease1Utc(challenge.ExpiresAtUtc, "challenge expiry") >=
                    now.ToUniversalTime())
                    pending.Add(challenge);
            }
            return pending;
        }
    }

    internal Release1ConvergenceChallenge? GetRelease1Challenge(string commandId)
    {
        if (!Guid.TryParseExact(commandId, "D", out _))
            throw new ArgumentException("Release 1 command id is invalid.", nameof(commandId));
        lock (_connLock)
        {
            using var transaction = _conn.BeginTransaction();
            var result = ReadRelease1Challenge(transaction, commandId, null);
            transaction.Commit();
            return result;
        }
    }

    internal PersistedRelease1Preliminary GetOrCreateRelease1Preliminary(
        string commandId,
        Release1PreliminaryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var requestBytes = Release1ConvergenceContract.CanonicalBytes(request);
        ValidateBoundedRequest(requestBytes);
        var requestJson = Encoding.UTF8.GetString(requestBytes);
        var requestSha256 = Sha256(requestBytes);
        var proof = request.Proof;
        ValidateBase64UrlP1363(request.ProofSignatureBase64Url, "preliminary signature");
        ValidateLowerSha256(proof.InstallReceiptSha256, "install receipt digest");
        ValidateLowerSha256(proof.RestartReceiptSha256, "restart receipt digest");
        _ = ParseRelease1Utc(proof.VerifiedAtUtc, "preliminary verification time");

        lock (_connLock)
        {
            using var transaction = _conn.BeginTransaction();
            var existing = ReadRelease1Preliminary(transaction, commandId);
            if (existing is not null)
            {
                transaction.Commit();
                if (!Release1FixedTextEquals(existing.RequestJson, requestJson) ||
                    !Release1FixedHexEquals(existing.RequestSha256, requestSha256))
                    throw new InvalidOperationException(
                        "Release 1 preliminary proof replay conflict.");
                return existing;
            }
            RequireRelease1Challenge(transaction, commandId);
            using var insert = CreateCommand(transaction, """
                INSERT INTO release1_convergence_preliminary_proofs (
                    command_id, request_json, request_sha256,
                    install_receipt_sha256, restart_receipt_sha256,
                    verified_at_utc, created_at_utc
                ) VALUES (
                    @commandId, @requestJson, @requestSha256,
                    @installReceiptSha256, @restartReceiptSha256,
                    @verifiedAtUtc, @createdAtUtc
                )
                """);
            insert.Parameters.AddWithValue("@commandId", commandId);
            insert.Parameters.AddWithValue("@requestJson", requestJson);
            insert.Parameters.AddWithValue("@requestSha256", requestSha256);
            insert.Parameters.AddWithValue(
                "@installReceiptSha256",
                proof.InstallReceiptSha256);
            insert.Parameters.AddWithValue(
                "@restartReceiptSha256",
                proof.RestartReceiptSha256);
            insert.Parameters.AddWithValue("@verifiedAtUtc", proof.VerifiedAtUtc);
            insert.Parameters.AddWithValue(
                "@createdAtUtc",
                Release1ConvergenceContract.ExactUtc(DateTimeOffset.UtcNow));
            insert.ExecuteNonQuery();
            transaction.Commit();
            return new(
                commandId,
                requestJson,
                requestSha256,
                request,
                proof.InstallReceiptSha256,
                proof.RestartReceiptSha256,
                proof.VerifiedAtUtc);
        }
    }

    internal PersistedRelease1Preliminary? GetRelease1Preliminary(string commandId)
    {
        lock (_connLock)
        {
            using var transaction = _conn.BeginTransaction();
            var result = ReadRelease1Preliminary(transaction, commandId);
            transaction.Commit();
            return result;
        }
    }

    internal PersistedRelease1Final GetOrCreateRelease1Final(
        string commandId,
        string noopCommandId,
        Release1FinalRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Guid.TryParseExact(noopCommandId, "D", out _))
            throw new InvalidOperationException("Release 1 no-op command id is invalid.");
        var requestBytes = Release1ConvergenceContract.CanonicalBytes(request);
        ValidateBoundedRequest(requestBytes);
        var requestJson = Encoding.UTF8.GetString(requestBytes);
        var requestSha256 = Sha256(requestBytes);
        ValidateBase64UrlP1363(
            request.AttestationSignatureBase64Url,
            "attestation signature");
        ValidateBase64UrlP1363(
            request.InstallReceiptSignatureBase64Url,
            "install receipt signature");
        _ = ParseRelease1Utc(
            request.Attestation.VerifiedAtUtc,
            "attestation verification time");

        lock (_connLock)
        {
            using var transaction = _conn.BeginTransaction();
            var existing = ReadRelease1Final(transaction, commandId);
            if (existing is not null)
            {
                transaction.Commit();
                if (!string.Equals(
                        existing.NoopCommandId,
                        noopCommandId,
                        StringComparison.Ordinal) ||
                    !Release1FixedTextEquals(existing.RequestJson, requestJson) ||
                    !Release1FixedHexEquals(existing.RequestSha256, requestSha256))
                    throw new InvalidOperationException(
                        "Release 1 final evidence replay conflict.");
                return existing;
            }
            RequireRelease1Preliminary(transaction, commandId);
            using var insert = CreateCommand(transaction, """
                INSERT INTO release1_convergence_final_evidence (
                    command_id, noop_command_id, request_json,
                    request_sha256, verified_at_utc, created_at_utc
                ) VALUES (
                    @commandId, @noopCommandId, @requestJson,
                    @requestSha256, @verifiedAtUtc, @createdAtUtc
                )
                """);
            insert.Parameters.AddWithValue("@commandId", commandId);
            insert.Parameters.AddWithValue("@noopCommandId", noopCommandId);
            insert.Parameters.AddWithValue("@requestJson", requestJson);
            insert.Parameters.AddWithValue("@requestSha256", requestSha256);
            insert.Parameters.AddWithValue(
                "@verifiedAtUtc",
                request.Attestation.VerifiedAtUtc);
            insert.Parameters.AddWithValue(
                "@createdAtUtc",
                Release1ConvergenceContract.ExactUtc(DateTimeOffset.UtcNow));
            insert.ExecuteNonQuery();
            transaction.Commit();
            return new(
                commandId,
                noopCommandId,
                requestJson,
                requestSha256,
                request,
                request.Attestation.VerifiedAtUtc);
        }
    }

    internal PersistedRelease1Final? GetRelease1Final(string commandId)
    {
        lock (_connLock)
        {
            using var transaction = _conn.BeginTransaction();
            var result = ReadRelease1Final(transaction, commandId);
            transaction.Commit();
            return result;
        }
    }

    internal Release1ConvergenceDelivery? GetRelease1Delivery(
        string commandId,
        string phase)
    {
        ValidateRelease1Phase(phase);
        lock (_connLock)
        {
            using var command = _conn.CreateCommand();
            command.CommandText = """
                SELECT request_sha256, response_command_id, accepted_at_utc
                  FROM release1_convergence_deliveries
                 WHERE command_id = @commandId AND phase = @phase
                 LIMIT 1
                """;
            command.Parameters.AddWithValue("@commandId", commandId);
            command.Parameters.AddWithValue("@phase", phase);
            using var reader = command.ExecuteReader();
            return reader.Read()
                ? new(
                    commandId,
                    phase,
                    reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.GetString(2))
                : null;
        }
    }

    internal void RecordRelease1Delivery(
        string commandId,
        string phase,
        string requestSha256,
        string? responseCommandId,
        DateTimeOffset acceptedAt)
    {
        ValidateRelease1Phase(phase);
        ValidateLowerSha256(requestSha256, "delivery request digest");
        if ((phase == "preliminary") != (responseCommandId is not null) ||
            responseCommandId is not null &&
            !Guid.TryParseExact(responseCommandId, "D", out _))
            throw new InvalidOperationException("Release 1 delivery response is invalid.");

        lock (_connLock)
        {
            using var transaction = _conn.BeginTransaction();
            var existing = ReadRelease1Delivery(transaction, commandId, phase);
            if (existing is not null)
            {
                transaction.Commit();
                if (!Release1FixedHexEquals(existing.RequestSha256, requestSha256) ||
                    !string.Equals(
                        existing.ResponseCommandId,
                        responseCommandId,
                        StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "Release 1 delivery replay conflict.");
                return;
            }
            RequireRelease1DeliveryPrerequisites(
                transaction,
                commandId,
                phase,
                requestSha256);
            using var insert = CreateCommand(transaction, """
                INSERT INTO release1_convergence_deliveries (
                    command_id, phase, request_sha256,
                    response_command_id, accepted_at_utc
                ) VALUES (
                    @commandId, @phase, @requestSha256,
                    @responseCommandId, @acceptedAtUtc
                )
                """);
            insert.Parameters.AddWithValue("@commandId", commandId);
            insert.Parameters.AddWithValue("@phase", phase);
            insert.Parameters.AddWithValue("@requestSha256", requestSha256);
            insert.Parameters.AddWithValue(
                "@responseCommandId",
                (object?)responseCommandId ?? DBNull.Value);
            insert.Parameters.AddWithValue(
                "@acceptedAtUtc",
                Release1ConvergenceContract.ExactUtc(acceptedAt));
            insert.ExecuteNonQuery();
            transaction.Commit();
        }
    }

    private Release1ConvergenceChallenge? ReadRelease1Challenge(
        SqliteTransaction transaction,
        string commandId,
        string? nonce)
    {
        using var select = CreateCommand(transaction, """
            SELECT command_id, envelope_nonce, command_name, agent_id,
                   machine_fingerprint, command_timestamp, command_data_hash,
                   command_key_id, command_signature, inventory_sha256,
                   bridge_release_tag, bridge_source_sha, expires_at_utc
              FROM release1_convergence_challenges
             WHERE command_id = @commandId
                OR (@nonce IS NOT NULL AND envelope_nonce = @nonce)
             LIMIT 1
            """);
        select.Parameters.AddWithValue("@commandId", commandId);
        select.Parameters.AddWithValue("@nonce", (object?)nonce ?? DBNull.Value);
        using var reader = select.ExecuteReader();
        return reader.Read() ? MaterializeRelease1Challenge(reader) : null;
    }

    private static Release1ConvergenceChallenge MaterializeRelease1Challenge(
        SqliteDataReader reader)
    {
        var expiresAt = reader.GetString(12);
        return new(
            reader.GetString(0),
            reader.GetString(9),
            reader.GetString(10),
            reader.GetString(11),
            expiresAt,
            new SignedCommand(
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(1),
                reader.GetString(7),
                reader.GetString(8),
                reader.GetString(6),
                expiresAt));
    }

    private PersistedRelease1Preliminary? ReadRelease1Preliminary(
        SqliteTransaction transaction,
        string commandId)
    {
        using var select = CreateCommand(transaction, """
            SELECT request_json, request_sha256, install_receipt_sha256,
                   restart_receipt_sha256, verified_at_utc
              FROM release1_convergence_preliminary_proofs
             WHERE command_id = @commandId
             LIMIT 1
            """);
        select.Parameters.AddWithValue("@commandId", commandId);
        using var reader = select.ExecuteReader();
        if (!reader.Read()) return null;
        var json = reader.GetString(0);
        var hash = reader.GetString(1);
        var installHash = reader.GetString(2);
        var restartHash = reader.GetString(3);
        var verifiedAt = reader.GetString(4);
        var request = DeserializeExactCanonical<Release1PreliminaryRequest>(json);
        if (!Release1FixedHexEquals(Sha256(Encoding.UTF8.GetBytes(json)), hash) ||
            !string.Equals(
                request.Proof.InstallReceiptSha256,
                installHash,
                StringComparison.Ordinal) ||
            !string.Equals(
                request.Proof.RestartReceiptSha256,
                restartHash,
                StringComparison.Ordinal) ||
            !string.Equals(
                request.Proof.VerifiedAtUtc,
                verifiedAt,
                StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Release 1 preliminary proof storage binding is invalid.");
        return new(
            commandId,
            json,
            hash,
            request,
            installHash,
            restartHash,
            verifiedAt);
    }

    private PersistedRelease1Final? ReadRelease1Final(
        SqliteTransaction transaction,
        string commandId)
    {
        using var select = CreateCommand(transaction, """
            SELECT noop_command_id, request_json, request_sha256, verified_at_utc
              FROM release1_convergence_final_evidence
             WHERE command_id = @commandId
             LIMIT 1
            """);
        select.Parameters.AddWithValue("@commandId", commandId);
        using var reader = select.ExecuteReader();
        if (!reader.Read()) return null;
        var noopCommandId = reader.GetString(0);
        var json = reader.GetString(1);
        var hash = reader.GetString(2);
        var verifiedAt = reader.GetString(3);
        var request = DeserializeExactCanonical<Release1FinalRequest>(json);
        if (!Release1FixedHexEquals(Sha256(Encoding.UTF8.GetBytes(json)), hash) ||
            !string.Equals(
                request.Attestation.VerifiedAtUtc,
                verifiedAt,
                StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Release 1 final evidence storage binding is invalid.");
        return new(commandId, noopCommandId, json, hash, request, verifiedAt);
    }

    private Release1ConvergenceDelivery? ReadRelease1Delivery(
        SqliteTransaction transaction,
        string commandId,
        string phase)
    {
        using var select = CreateCommand(transaction, """
            SELECT request_sha256, response_command_id, accepted_at_utc
              FROM release1_convergence_deliveries
             WHERE command_id = @commandId AND phase = @phase
             LIMIT 1
            """);
        select.Parameters.AddWithValue("@commandId", commandId);
        select.Parameters.AddWithValue("@phase", phase);
        using var reader = select.ExecuteReader();
        return reader.Read()
            ? new(
                commandId,
                phase,
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetString(2))
            : null;
    }

    private static void ValidateRelease1Challenge(
        Release1ConvergenceChallenge challenge,
        DateTimeOffset now)
    {
        var envelope = challenge.Envelope;
        var issuedAt = ParseRelease1RoundtripUtc(
            envelope.Timestamp,
            "challenge command timestamp");
        var expiresAt = ParseRelease1Utc(
            challenge.ExpiresAtUtc,
            "challenge expiry");
        if (!Guid.TryParseExact(challenge.CommandId, "D", out _) ||
            !string.Equals(
                envelope.Command,
                Release1ConvergenceCommand.Name,
                StringComparison.Ordinal) ||
            !IsSafeToken(envelope.AgentId, 160) ||
            !IsSafeToken(envelope.MachineFingerprint, 256) ||
            !IsSafeToken(envelope.Nonce, 160) ||
            !IsLowerSha256(envelope.DataHash) ||
            !IsSafeToken(envelope.KeyId, 80) ||
            !IsBase64P1363(envelope.Signature) ||
            !IsLowerSha256(challenge.InventorySha256) ||
            !IsSafeToken(challenge.BridgeReleaseTag, 80) ||
            !IsLowerHex(challenge.BridgeSourceSha, 40) ||
            !string.Equals(
                envelope.ExpiresAt,
                challenge.ExpiresAtUtc,
                StringComparison.Ordinal) ||
            expiresAt <= now.ToUniversalTime() ||
            expiresAt <= issuedAt ||
            expiresAt - issuedAt > MaximumRelease1ChallengeLifetime)
            throw new InvalidOperationException("Release 1 challenge is invalid.");
    }

    private static T DeserializeExactCanonical<T>(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) is <= 0 or > MaxRelease1RequestBytes)
            throw new InvalidOperationException("Release 1 request exceeds its bound.");
        T value;
        try
        {
            value = JsonSerializer.Deserialize<T>(
                json,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new InvalidOperationException("Release 1 request is null.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                "Release 1 request JSON is invalid.",
                exception);
        }
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(json),
                Release1ConvergenceContract.CanonicalBytes(value)))
            throw new InvalidOperationException("Release 1 request is not canonical.");
        return value;
    }

    private void RequireRelease1Challenge(
        SqliteTransaction transaction,
        string commandId)
    {
        using var select = CreateCommand(transaction, """
            SELECT 1 FROM release1_convergence_challenges
             WHERE command_id = @commandId LIMIT 1
            """);
        select.Parameters.AddWithValue("@commandId", commandId);
        if (select.ExecuteScalar() is null)
            throw new InvalidOperationException("Release 1 challenge is not registered.");
    }

    private void RequireRelease1Preliminary(
        SqliteTransaction transaction,
        string commandId)
    {
        using var select = CreateCommand(transaction, """
            SELECT 1 FROM release1_convergence_preliminary_proofs
             WHERE command_id = @commandId LIMIT 1
            """);
        select.Parameters.AddWithValue("@commandId", commandId);
        if (select.ExecuteScalar() is null)
            throw new InvalidOperationException("Release 1 preliminary proof is missing.");
    }

    private void RequireRelease1DeliveryPrerequisites(
        SqliteTransaction transaction,
        string commandId,
        string phase,
        string requestSha256)
    {
        if (phase == "challenge_ack")
        {
            RequireRelease1Challenge(transaction, commandId);
            return;
        }
        using var prior = CreateCommand(transaction, """
            SELECT 1 FROM release1_convergence_deliveries
             WHERE command_id = @commandId AND phase = 'challenge_ack'
             LIMIT 1
            """);
        prior.Parameters.AddWithValue("@commandId", commandId);
        if (prior.ExecuteScalar() is null)
            throw new InvalidOperationException("Release 1 challenge ACK is missing.");

        var evidenceSql = phase == "preliminary"
            ? """
              SELECT 1 FROM release1_convergence_preliminary_proofs
               WHERE command_id = @commandId AND request_sha256 = @requestSha256
               LIMIT 1
              """
            : """
              SELECT 1 FROM release1_convergence_final_evidence
               WHERE command_id = @commandId AND request_sha256 = @requestSha256
               LIMIT 1
              """;
        using var evidence = CreateCommand(transaction, evidenceSql);
        evidence.Parameters.AddWithValue("@commandId", commandId);
        evidence.Parameters.AddWithValue("@requestSha256", requestSha256);
        if (evidence.ExecuteScalar() is null)
            throw new InvalidOperationException("Release 1 delivery evidence is missing.");
        if (phase == "final")
        {
            using var preliminary = CreateCommand(transaction, """
                SELECT 1 FROM release1_convergence_deliveries
                 WHERE command_id = @commandId AND phase = 'preliminary'
                 LIMIT 1
                """);
            preliminary.Parameters.AddWithValue("@commandId", commandId);
            if (preliminary.ExecuteScalar() is null)
                throw new InvalidOperationException(
                    "Release 1 preliminary delivery is missing.");
        }
    }

    private static void ValidateRelease1Phase(string phase)
    {
        if (phase is not ("challenge_ack" or "preliminary" or "final"))
            throw new ArgumentException("Release 1 delivery phase is invalid.", nameof(phase));
    }

    private static DateTimeOffset ParseRelease1Utc(string value, string label)
    {
        if (!DateTimeOffset.TryParseExact(
                value,
                "yyyy-MM-dd'T'HH:mm:ss'Z'",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed) ||
            !string.Equals(
                Release1ConvergenceContract.ExactUtc(parsed),
                value,
                StringComparison.Ordinal))
            throw new InvalidOperationException($"Release 1 {label} is invalid.");
        return parsed;
    }

    private static DateTimeOffset ParseRelease1RoundtripUtc(string value, string label)
    {
        if (!DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsed))
            throw new InvalidOperationException($"Release 1 {label} is invalid.");
        return parsed.ToUniversalTime();
    }

    private static void ValidateBoundedRequest(byte[] requestBytes)
    {
        if (requestBytes.Length is <= 0 or > MaxRelease1RequestBytes)
            throw new InvalidOperationException("Release 1 request exceeds its bound.");
    }

    private static void ValidateLowerSha256(string value, string label)
    {
        if (!IsLowerSha256(value))
            throw new InvalidOperationException($"Release 1 {label} is invalid.");
    }

    private static void ValidateBase64UrlP1363(string value, string label)
    {
        if (!IsBase64UrlP1363(value))
            throw new InvalidOperationException($"Release 1 {label} is invalid.");
    }

    private static string Sha256(ReadOnlySpan<byte> value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    private static bool Release1FixedTextEquals(string? left, string? right) =>
        left is not null && right is not null &&
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(left),
            Encoding.UTF8.GetBytes(right));

    private static bool Release1FixedHexEquals(string? left, string? right) =>
        left is not null && right is not null && left.Length == right.Length &&
        CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(left),
            Encoding.ASCII.GetBytes(right));
}
