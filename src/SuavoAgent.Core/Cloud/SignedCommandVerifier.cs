using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SuavoAgent.Contracts.Maintenance;

namespace SuavoAgent.Core.Cloud;

public record SignedCommand(
    string Command, string AgentId, string MachineFingerprint,
    string Timestamp, string Nonce, string KeyId, string Signature,
    string DataHash = "", string? ExpiresAt = null);

public record VerificationResult(bool IsValid, string? Reason = null);

public enum SignedCommandAuthorityClass
{
    LiveMutator,
    ReadOnly,
    DurableOutbox,
    RetiredNoOp,
}

public class SignedCommandVerifier
{
    private readonly Dictionary<string, ECDsa> _keys = new();
    private readonly string _agentId;
    private readonly string _fingerprint;
    private readonly Dictionary<string, DateTimeOffset> _usedNonces = new();
    private readonly TimeSpan _timestampWindow = TimeSpan.FromSeconds(30);
    private readonly TimeProvider _timeProvider;
    private static readonly TimeSpan MaximumLiveCommandLifetime = TimeSpan.FromMinutes(5);
    private static readonly IReadOnlyDictionary<string, SignedCommandAuthorityClass>
        KnownCommandAuthority = new Dictionary<string, SignedCommandAuthorityClass>(
            StringComparer.Ordinal)
    {
        ["fetch_patient"] = SignedCommandAuthorityClass.LiveMutator,
        ["delivery_writeback"] = SignedCommandAuthorityClass.LiveMutator,
        ["repair"] = SignedCommandAuthorityClass.LiveMutator,
        ["repair_agent"] = SignedCommandAuthorityClass.LiveMutator,
        ["export_pioneerrx_shadow_fixture"] = SignedCommandAuthorityClass.LiveMutator,
        ["acknowledge_drift"] = SignedCommandAuthorityClass.LiveMutator,
        ["approve_candidate"] = SignedCommandAuthorityClass.LiveMutator,
        ["reject_candidate"] = SignedCommandAuthorityClass.LiveMutator,
        ["reapprove_candidate"] = SignedCommandAuthorityClass.LiveMutator,
        ["force_relearn"] = SignedCommandAuthorityClass.LiveMutator,
        ["adjust_window"] = SignedCommandAuthorityClass.LiveMutator,
        ["acknowledge_stale"] = SignedCommandAuthorityClass.LiveMutator,
        ["find_and_run_pricing_job"] = SignedCommandAuthorityClass.LiveMutator,
        ["run_pricing_job"] = SignedCommandAuthorityClass.LiveMutator,
        ["show_cursor"] = SignedCommandAuthorityClass.LiveMutator,
        ["show_intent_cursor"] = SignedCommandAuthorityClass.LiveMutator,
        ["transition_auto_rule_approval"] = SignedCommandAuthorityClass.LiveMutator,
        ["run_workflow"] = SignedCommandAuthorityClass.LiveMutator,
        ["abort_workflow"] = SignedCommandAuthorityClass.LiveMutator,
        ["update_selector"] = SignedCommandAuthorityClass.LiveMutator,
        ["navigate_app"] = SignedCommandAuthorityClass.LiveMutator,
        ["navigate_pricing"] = SignedCommandAuthorityClass.LiveMutator,
        ["replay_template"] = SignedCommandAuthorityClass.LiveMutator,
        ["run_learned_template"] = SignedCommandAuthorityClass.LiveMutator,
        ["explore_sandbox"] = SignedCommandAuthorityClass.LiveMutator,
        ["replay_skill"] = SignedCommandAuthorityClass.LiveMutator,
        ["abort_navigation"] = SignedCommandAuthorityClass.LiveMutator,
        ["force_restart"] = SignedCommandAuthorityClass.LiveMutator,
        ["force_learning_phase"] = SignedCommandAuthorityClass.LiveMutator,
        ["extend_app_allowlist"] = SignedCommandAuthorityClass.LiveMutator,
        ["set_reasoning_config"] = SignedCommandAuthorityClass.LiveMutator,
        ["restart_helper"] = SignedCommandAuthorityClass.LiveMutator,
        ["self_uninstall"] = SignedCommandAuthorityClass.LiveMutator,

        ["collect_health_probe"] = SignedCommandAuthorityClass.ReadOnly,
        ["fetch_diagnostics"] = SignedCommandAuthorityClass.ReadOnly,
        ["computer_use_observe"] = SignedCommandAuthorityClass.ReadOnly,
        ["computer_use_propose"] = SignedCommandAuthorityClass.ReadOnly,
        ["discover_elements"] = SignedCommandAuthorityClass.ReadOnly,
        ["chat"] = SignedCommandAuthorityClass.ReadOnly,

        ["update"] = SignedCommandAuthorityClass.DurableOutbox,
        ["approve_pom"] = SignedCommandAuthorityClass.DurableOutbox,
        ["install_pioneerrx_process_approval"] = SignedCommandAuthorityClass.DurableOutbox,
        ["set_vision_config"] = SignedCommandAuthorityClass.DurableOutbox,
        ["install_pricing_cost_basis_approval"] = SignedCommandAuthorityClass.DurableOutbox,
        ["revoke_pricing_cost_basis_approval"] = SignedCommandAuthorityClass.DurableOutbox,

        ["decommission"] = SignedCommandAuthorityClass.RetiredNoOp,
    };

    public static SignedCommandAuthorityClass ClassifyCommand(string command) =>
        KnownCommandAuthority.TryGetValue(command, out var classification)
            ? classification
            : SignedCommandAuthorityClass.LiveMutator;

    public static bool IsExplicitlyClassified(string command) =>
        KnownCommandAuthority.ContainsKey(command);

    public static bool RequiresLiveExpiry(string command) =>
        ClassifyCommand(command) == SignedCommandAuthorityClass.LiveMutator;

    public SignedCommandVerifier(
        Dictionary<string, string> keyRegistry,
        string agentId, string fingerprint,
        TimeProvider? timeProvider = null)
    {
        _agentId = agentId;
        _fingerprint = fingerprint;
        _timeProvider = timeProvider ?? TimeProvider.System;

        foreach (var (keyId, pubKeyDer) in keyRegistry)
        {
            var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(pubKeyDer), out _);
            _keys[keyId] = ecdsa;
        }
    }

    public VerificationResult Verify(SignedCommand cmd, bool consumeNonce = true)
    {
        if (string.IsNullOrWhiteSpace(cmd.Command))
            return new(false, "Missing command");

        if (string.IsNullOrWhiteSpace(cmd.Nonce))
            return new(false, "Missing nonce");

        if (string.IsNullOrWhiteSpace(cmd.Signature))
            return new(false, "Missing signature");

        if (!_keys.TryGetValue(cmd.KeyId, out var key))
            return new(false, $"Unknown keyId: {cmd.KeyId}");

        if (!string.Equals(cmd.AgentId, _agentId, StringComparison.Ordinal))
            return new(false, "AgentId mismatch");

        if (!string.Equals(cmd.MachineFingerprint, _fingerprint, StringComparison.Ordinal))
            return new(false, "Fingerprint mismatch");

        if (!DateTimeOffset.TryParse(cmd.Timestamp, out var ts))
            return new(false, "Invalid timestamp format");
        var now = _timeProvider.GetUtcNow();
        var skew = (now - ts).Duration();
        if (skew > _timestampWindow)
            return new(false, "Timestamp out of window");

        var expiry = ValidateLiveCommandExpiry(cmd, ts, now, validateLifetime: true);
        if (!expiry.IsValid)
            return expiry;

        lock (_usedNonces)
        {
            if (_usedNonces.ContainsKey(cmd.Nonce))
                return new(false, "Nonce replay detected");
        }

        var dataHash = string.IsNullOrEmpty(cmd.DataHash) ? "" : cmd.DataHash;
        var canonical = RemoteCommandTrust.BuildCommandCanonical(
            cmd.Command,
            cmd.AgentId,
            cmd.MachineFingerprint,
            cmd.Timestamp,
            cmd.Nonce,
            dataHash);
        try
        {
            var valid = key.VerifyData(
                Encoding.UTF8.GetBytes(canonical),
                Convert.FromBase64String(cmd.Signature),
                HashAlgorithmName.SHA256);
            if (!valid)
                return new(false, "Invalid signature");

            if (consumeNonce)
            {
                lock (_usedNonces)
                {
                    if (_usedNonces.ContainsKey(cmd.Nonce))
                        return new(false, "Nonce replay detected");
                    _usedNonces[cmd.Nonce] = now;
                }
            }

            return new(true);
        }
        catch
        {
            return new(false, "Signature verification error");
        }
    }

    /// <summary>
    /// Re-checks a short-lived desktop authority at the executor boundary.
    /// Verification and scheduling are separate moments; this prevents a valid
    /// command from waiting in a thread-pool queue until after its authorization
    /// has expired.
    /// </summary>
    public VerificationResult VerifyExecutionAuthority(SignedCommand cmd) =>
        VerifyExecutionAuthorityAt(
            cmd,
            _timeProvider.GetUtcNow());

    public static VerificationResult VerifyExecutionAuthorityAt(
        SignedCommand cmd,
        DateTimeOffset now) =>
        ValidateLiveCommandExpiry(
            cmd, issuedAt: null, now: now, validateLifetime: false);

    public static VerificationResult VerifyExecutionAuthorityAt(
        SignedCommand cmd,
        DateTimeOffset now,
        TimeSpan minimumRemaining)
    {
        if (minimumRemaining < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(minimumRemaining));
        var current = ValidateLiveCommandExpiry(
            cmd, issuedAt: null, now: now, validateLifetime: false);
        if (!current.IsValid || !RequiresLiveExpiry(cmd.Command) ||
            minimumRemaining == TimeSpan.Zero)
            return current;
        if (!DateTimeOffset.TryParse(
                cmd.ExpiresAt,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var expiresAt) || expiresAt <= now + minimumRemaining)
            return new(false, "Live command authority handoff runway insufficient");
        return new(true);
    }

    private static VerificationResult ValidateLiveCommandExpiry(
        SignedCommand cmd,
        DateTimeOffset? issuedAt,
        DateTimeOffset now,
        bool validateLifetime)
    {
        if (!RequiresLiveExpiry(cmd.Command))
            return new(true);
        if (string.IsNullOrWhiteSpace(cmd.ExpiresAt))
            return new(false, "Live command expiry missing");
        if (!DateTimeOffset.TryParse(
                cmd.ExpiresAt,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var expiresAt))
            return new(false, "Live command expiry invalid");
        if (expiresAt <= now)
            return new(false, "Live command authority expired");
        if (validateLifetime && issuedAt is not null &&
            (expiresAt <= issuedAt.Value ||
             expiresAt - issuedAt.Value > MaximumLiveCommandLifetime))
            return new(false, "Live command expiry out of bounds");
        return new(true);
    }

    /// <summary>
    /// Commits a nonce after a deferred, crash-resumable command reaches its terminal
    /// receipt. The signature has already been verified by <see cref="Verify"/> with
    /// <c>consumeNonce:false</c>; this method never admits a command by itself.
    /// </summary>
    public bool TryConsumeVerifiedNonce(string nonce)
    {
        if (string.IsNullOrWhiteSpace(nonce)) return false;
        lock (_usedNonces)
        {
            if (_usedNonces.ContainsKey(nonce)) return false;
            _usedNonces[nonce] = _timeProvider.GetUtcNow();
            return true;
        }
    }

    public void PruneNonces(TimeSpan maxAge)
    {
        var cutoff = _timeProvider.GetUtcNow() - maxAge;
        lock (_usedNonces)
        {
            var expired = _usedNonces.Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToList();
            foreach (var key in expired) _usedNonces.Remove(key);
        }
    }

    /// <summary>
    /// Computes SHA-256 hex-lowercase hash of raw JSON data for canonical inclusion.
    /// Returns the hash of an empty string when <paramref name="dataJson"/> is null or empty.
    /// </summary>
    public static string ComputeDataHash(string? dataJson)
    {
        return RemoteCommandTrust.ComputeSha256Hex(dataJson);
    }
}
