using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using SuavoAgent.Contracts.Behavioral;
using SuavoAgent.Contracts.Canary;
using SuavoAgent.Contracts.Learning;
using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Core.Behavioral;
using SuavoAgent.Core.Learning;
using SuavoAgent.Core.Pricing;

namespace SuavoAgent.Core.State;

public sealed partial class AgentStateDb
{
    private readonly object _pricingCandidateMemoryLock = new();
    private ImmutableDictionary<string, PricingDiscoveryCandidate>
        _pricingDiscoveryCandidates =
            ImmutableDictionary<string, PricingDiscoveryCandidate>.Empty;

    public void Dispose()
    {
        _conn.Dispose();
    }

    private string HmacRxNumber(string rxNumber)
    {
        using var hmac = new HMACSHA256(Convert.FromBase64String(_auditChainSeed));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(rxNumber ?? "")));
    }

    private static string? EncryptRxNumber(string rxNumber)
    {
        if (!OperatingSystem.IsWindows()) return rxNumber;
        return EncryptRxNumberWindows(rxNumber);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string EncryptRxNumberWindows(string rxNumber)
    {
        var plain = Encoding.UTF8.GetBytes(rxNumber ?? "");
        var enc = System.Security.Cryptography.ProtectedData.Protect(
            plain, null, System.Security.Cryptography.DataProtectionScope.LocalMachine);
        return Convert.ToBase64String(enc);
    }

    private static string? DecryptRxNumber(string? enc)
    {
        if (string.IsNullOrEmpty(enc)) return null;
        if (!OperatingSystem.IsWindows()) return enc;
        return DecryptRxNumberWindows(enc);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string? DecryptRxNumberWindows(string enc)
    {
        try
        {
            var bytes = Convert.FromBase64String(enc);
            var plain = System.Security.Cryptography.ProtectedData.Unprotect(
                bytes, null, System.Security.Cryptography.DataProtectionScope.LocalMachine);
            return Encoding.UTF8.GetString(plain);
        }
        catch { return null; }
    }

    // ── Pricing jobs ──────────────────────────────────────────────────────────

    public void UpsertPricingJob(PricingJobSpec spec, string status, int total, int completed, int failed)
        => UpsertPricingJobAtomic(spec, status, total, completed, failed);

    public void SavePricingResult(SupplierPriceResult result)
    {
        result = NormalizeSelectorObservationsForPersistence(result);
        result = PricingResultContentPolicy.NormalizeForPersistence(result);
        lock (_connLock)
        {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO pricing_results
                (job_id, row_index, ndc, found, supplier_name, cost_per_unit,
                 baseline_cost_per_unit, quantity, error_message,
                 observations_json, omitted_selector_observations)
            VALUES (@job, @row, @ndc, @found, @supplier, @cost, @baseline,
                    @quantity, @error, @observations, @omitted_observations)
            """;
        var observationsJson = result.Observations is { Count: > 0 }
            ? System.Text.Json.JsonSerializer.Serialize(result.Observations)
            : null;
        cmd.Parameters.AddWithValue("@job", result.JobId);
        cmd.Parameters.AddWithValue("@row", result.RowIndex);
        cmd.Parameters.AddWithValue("@ndc", result.Ndc);
        cmd.Parameters.AddWithValue("@found", result.Found ? 1 : 0);
        cmd.Parameters.AddWithValue("@supplier", (object?)result.SupplierName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@cost", (object?)result.CostPerUnit ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@baseline", (object?)result.BaselineCostPerUnit ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@quantity", (object?)result.Quantity ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@error", (object?)result.ErrorMessage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@observations", (object?)observationsJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue(
            "@omitted_observations", result.OmittedSelectorObservations);
        cmd.ExecuteNonQuery();
        }
    }

    private static SupplierPriceResult NormalizeSelectorObservationsForPersistence(
        SupplierPriceResult result)
    {
        var total = (long)result.OmittedSelectorObservations +
            (result.Observations?.Count ?? 0);
        if (result.OmittedSelectorObservations < 0 ||
            total > PricingSelectorObservationPolicy.MaximumTotalObservations)
            throw new InvalidOperationException(
                "pricing_result_selector_observations_out_of_range");
        var sanitized = result.Observations is { Count: > 0 }
            ? result.Observations
                .Select(SanitizeSelectorObservation)
                .Where(observation => observation is not null)
                .Cast<SelectorObservation>()
                .Take(PricingSelectorObservationPolicy
                    .MaximumStoredObservationsPerResult)
                .ToArray()
            : [];
        return result with
        {
            Observations = sanitized.Length == 0 ? null : sanitized,
            OmittedSelectorObservations = (int)total - sanitized.Length,
        };
    }

    private static SelectorObservation? SanitizeSelectorObservation(
        SelectorObservation observation)
    {
        if (!Enum.IsDefined(observation.StepId) ||
            !Enum.IsDefined(observation.ResolvedVia) ||
            !Enum.IsDefined(observation.Outcome) ||
            !Enum.IsDefined(observation.FailureKind) ||
            observation.ObservedCandidates is null ||
            observation.ObservedCandidates.Count >
                PricingSelectorObservationPolicy.MaximumCandidatesPerObservation)
            return null;
        var attempted = SanitizeObservedElement(observation.Attempted);
        if (observation.Attempted is not null && attempted is null)
            return null;
        var candidates = observation.ObservedCandidates
            .Select(SanitizeObservedElement)
            .ToArray();
        if (candidates.Any(candidate => candidate is null))
            return null;
        return observation with
        {
            Attempted = attempted,
            ObservedCandidates = candidates.Cast<ObservedElement>().ToArray(),
        };
    }

    private static ObservedElement? SanitizeObservedElement(ObservedElement? element)
    {
        if (element is null ||
            !StructuralIdentifierSanitizer.IsAllowed(element.ControlType) ||
            element.AutomationId is not null &&
                !StructuralIdentifierSanitizer.IsAllowed(element.AutomationId) ||
            element.ClassName is not null &&
                !StructuralIdentifierSanitizer.IsAllowed(element.ClassName))
            return null;
        var automationId = StructuralIdentifierSanitizer.AllowOrNull(element.AutomationId);
        var className = StructuralIdentifierSanitizer.AllowOrNull(element.ClassName);
        return new ObservedElement(element.ControlType, automationId, className);
    }

    public List<SupplierPriceResult> GetPricingResults(string jobId)
    {
        lock (_connLock)
        {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT job_id, row_index, ndc, found, supplier_name, cost_per_unit,
                   baseline_cost_per_unit, quantity, error_message,
                   observations_json, omitted_selector_observations
              FROM pricing_results
             WHERE job_id = @job
             ORDER BY row_index
            """;
        cmd.Parameters.AddWithValue("@job", jobId);
        using var reader = cmd.ExecuteReader();
        var results = new List<SupplierPriceResult>();
        while (reader.Read())
        {
            results.Add(new SupplierPriceResult(
                JobId: reader.GetString(0),
                RowIndex: reader.GetInt32(1),
                Ndc: reader.GetString(2),
                Found: reader.GetInt32(3) == 1,
                SupplierName: reader.IsDBNull(4) ? null : reader.GetString(4),
                CostPerUnit: reader.IsDBNull(5) ? null : (decimal)reader.GetDouble(5),
                ErrorMessage: reader.IsDBNull(8) ? null : reader.GetString(8),
                Observations: reader.IsDBNull(9)
                    ? null
                    : System.Text.Json.JsonSerializer.Deserialize<List<SuavoAgent.Contracts.Learning.SelectorObservation>>(reader.GetString(9)),
                BaselineCostPerUnit: reader.IsDBNull(6) ? null : (decimal)reader.GetDouble(6),
                Quantity: reader.IsDBNull(7) ? null : (decimal)reader.GetDouble(7),
                OmittedSelectorObservations: reader.GetInt32(10)));
        }
        return results;
        }
    }

    public HashSet<int> GetCompletedPricingRows(string jobId)
    {
        lock (_connLock)
        {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT row_index FROM pricing_results WHERE job_id = @job";
        cmd.Parameters.AddWithValue("@job", jobId);
        using var reader = cmd.ExecuteReader();
        var rows = new HashSet<int>();
        while (reader.Read()) rows.Add(reader.GetInt32(0));
        return rows;
        }
    }

    // ── M3 task-autonomy ledger ──

    /// <summary>Raw streak/total/last-outcome for a (task, pharmacy); zeros if never recorded.</summary>
    public (int ConsecutiveClean, int TotalRuns, string? LastOutcome) GetTaskAutonomyRaw(
        string taskKey, string pharmacyId)
    {
        lock (_connLock)
        {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText =
            "SELECT consecutive_clean, total_runs, last_outcome FROM task_autonomy WHERE task_key = @t AND pharmacy_id = @p";
        cmd.Parameters.AddWithValue("@t", taskKey);
        cmd.Parameters.AddWithValue("@p", pharmacyId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return (0, 0, null);
        return (
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.IsDBNull(2) ? null : reader.GetString(2));
        }
    }

    public void UpsertTaskAutonomy(
        string taskKey, string pharmacyId, int consecutiveClean, int totalRuns, string? lastOutcome)
    {
        lock (_connLock)
        {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO task_autonomy (task_key, pharmacy_id, consecutive_clean, total_runs, last_outcome, updated_at)
            VALUES (@t, @p, @c, @n, @o, datetime('now'))
            ON CONFLICT(task_key, pharmacy_id) DO UPDATE SET
                consecutive_clean = @c, total_runs = @n, last_outcome = @o, updated_at = datetime('now')
            """;
        cmd.Parameters.AddWithValue("@t", taskKey);
        cmd.Parameters.AddWithValue("@p", pharmacyId);
        cmd.Parameters.AddWithValue("@c", consecutiveClean);
        cmd.Parameters.AddWithValue("@n", totalRuns);
        cmd.Parameters.AddWithValue("@o", (object?)lastOutcome ?? DBNull.Value);
        cmd.ExecuteNonQuery();
        }
    }

    // ── Physarum edge-conductance store (durable slime-mold exploration memory) ──
    // Primitive (no Agentic types) so this layer stays decoupled from the Agentic contracts;
    // AgentStateDbEdgeConductanceStore adapts these to IEdgeConductanceStore / EdgeKey.

    /// <summary>Conductance for one (pharmacy, task, state, action) edge, or <paramref name="absent"/> if never seen.</summary>
    public double GetEdgeConductance(string pharmacyId, string taskKey, string stateHash, string actionSig, double absent)
    {
        lock (_edgeConductanceLock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT conductance FROM edge_conductance
                WHERE pharmacy_id = @p AND task_key = @t AND state_hash = @s AND action_sig = @a
                """;
            cmd.Parameters.AddWithValue("@p", pharmacyId);
            cmd.Parameters.AddWithValue("@t", taskKey);
            cmd.Parameters.AddWithValue("@s", stateHash);
            cmd.Parameters.AddWithValue("@a", actionSig);
            var v = cmd.ExecuteScalar();
            return v is null or DBNull ? absent : Convert.ToDouble(v);
        }
    }

    /// <summary>Persist an already-clamped conductance for one edge (callers clamp via ConductanceLaw).</summary>
    public void SetEdgeConductance(string pharmacyId, string taskKey, string stateHash, string actionSig, double conductance)
    {
        lock (_edgeConductanceLock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO edge_conductance (pharmacy_id, task_key, state_hash, action_sig, conductance, updated_at)
                VALUES (@p, @t, @s, @a, @c, datetime('now'))
                ON CONFLICT(pharmacy_id, task_key, state_hash, action_sig) DO UPDATE SET
                    conductance = @c, updated_at = datetime('now')
                """;
            cmd.Parameters.AddWithValue("@p", pharmacyId);
            cmd.Parameters.AddWithValue("@t", taskKey);
            cmd.Parameters.AddWithValue("@s", stateHash);
            cmd.Parameters.AddWithValue("@a", actionSig);
            cmd.Parameters.AddWithValue("@c", conductance);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>All (state_hash, action_sig) edges for a (pharmacy, task) — drives the evaporation sweep.</summary>
    public IReadOnlyList<(string StateHash, string ActionSig)> GetEdgeConductanceKeys(string pharmacyId, string taskKey)
    {
        lock (_edgeConductanceLock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT state_hash, action_sig FROM edge_conductance
                WHERE pharmacy_id = @p AND task_key = @t
                """;
            cmd.Parameters.AddWithValue("@p", pharmacyId);
            cmd.Parameters.AddWithValue("@t", taskKey);
            using var reader = cmd.ExecuteReader();
            var keys = new List<(string, string)>();
            while (reader.Read()) keys.Add((reader.GetString(0), reader.GetString(1)));
            return keys;
        }
    }

    /// <summary>Distinct (pharmacy_id, task_key) scopes that have at least one edge — drives the
    /// evaporation worker's per-scope sweep.</summary>
    public IReadOnlyList<(string PharmacyId, string TaskKey)> GetAllEdgeConductancePharmacyTaskPairs()
    {
        lock (_edgeConductanceLock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT pharmacy_id, task_key FROM edge_conductance";
            using var reader = cmd.ExecuteReader();
            var pairs = new List<(string, string)>();
            while (reader.Read()) pairs.Add((reader.GetString(0), reader.GetString(1)));
            return pairs;
        }
    }

    // ── Verified-skill store (amortize ratchet) ──
    // Primitive (no Agentic types) so the DB layer stays decoupled; VerifiedSkill (de)serializes its own steps.

    /// <summary>Insert a banked verified skill, or — if this exact (skill_id) path was banked before —
    /// increment its success_count (the tube thickening). Returns the new success_count.</summary>
    public int UpsertVerifiedSkill(
        string skillId, string pharmacyId, string taskKey, string app, string stepsJson, string stepsHash)
    {
        lock (_verifiedSkillLock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO verified_skills
                    (skill_id, pharmacy_id, task_key, app, steps_json, steps_hash, success_count, first_verified_at, last_verified_at)
                VALUES (@id, @ph, @t, @app, @json, @hash, 1, datetime('now'), datetime('now'))
                ON CONFLICT(skill_id) DO UPDATE SET
                    success_count = success_count + 1,
                    last_verified_at = datetime('now'),
                    consecutive_failures = 0,
                    retired_at = NULL,
                    retirement_reason = NULL
                RETURNING success_count
                """;
            cmd.Parameters.AddWithValue("@id", skillId);
            cmd.Parameters.AddWithValue("@ph", pharmacyId);
            cmd.Parameters.AddWithValue("@t", taskKey);
            cmd.Parameters.AddWithValue("@app", app);
            cmd.Parameters.AddWithValue("@json", stepsJson);
            cmd.Parameters.AddWithValue("@hash", stepsHash);
            var v = cmd.ExecuteScalar();
            return v is null or DBNull ? 0 : Convert.ToInt32(v);
        }
    }

    /// <summary>Raw row for a banked skill (success_count + steps), or null if never banked.</summary>
    public (int SuccessCount, string StepsJson, string StepsHash)? GetVerifiedSkillRaw(string skillId)
    {
        lock (_verifiedSkillLock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT success_count, steps_json, steps_hash FROM verified_skills WHERE skill_id = @id";
            cmd.Parameters.AddWithValue("@id", skillId);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;
            return (reader.GetInt32(0), reader.GetString(1), reader.GetString(2));
        }
    }

    /// <summary>Full banked skill by id (for replay), or null if absent.</summary>
    public (string PharmacyId, string TaskKey, string App, string StepsJson, string StepsHash, int SuccessCount)? GetVerifiedSkill(string skillId)
    {
        lock (_verifiedSkillLock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT pharmacy_id, task_key, app, steps_json, steps_hash, success_count FROM verified_skills WHERE skill_id = @id AND retired_at IS NULL";
            cmd.Parameters.AddWithValue("@id", skillId);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;
            return (reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetInt32(5));
        }
    }

    /// <summary>The most-confirmed (highest success_count) banked skill for a (pharmacy, task, app) — the
    /// thickest tube to replay for a known task. Null if none banked yet.</summary>
    public (string SkillId, string StepsJson, string StepsHash, int SuccessCount)? GetBestVerifiedSkillForTask(
        string pharmacyId, string taskKey, string app)
    {
        lock (_verifiedSkillLock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT skill_id, steps_json, steps_hash, success_count FROM verified_skills
                WHERE pharmacy_id = @ph AND task_key = @t AND app = @app AND retired_at IS NULL
                ORDER BY success_count DESC, last_verified_at DESC
                LIMIT 1
                """;
            cmd.Parameters.AddWithValue("@ph", pharmacyId);
            cmd.Parameters.AddWithValue("@t", taskKey);
            cmd.Parameters.AddWithValue("@app", app);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;
            return (reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3));
        }
    }

    /// <summary>
    /// Feed a replay outcome back into the skill (slime-mold hygiene). A SUCCESS resets the failure streak
    /// and re-thickens (success_count++). A FAILURE accrues a consecutive-failure streak; once it reaches
    /// <paramref name="retireAfterConsecutiveFailures"/> the skill is RETIRED (excluded from selection) so a
    /// stale/drifted path stops being replayed and exploration re-learns it. A later re-harvest revives it
    /// (see <see cref="UpsertVerifiedSkill"/>). Returns the post-update state.
    /// </summary>
    public (int SuccessCount, int ConsecutiveFailures, bool Retired) RecordSkillReplayOutcome(
        string skillId, bool success, int retireAfterConsecutiveFailures = 3)
    {
        lock (_verifiedSkillLock)
        {
            using (var cmd = _conn.CreateCommand())
            {
                if (success)
                {
                    cmd.CommandText = """
                        UPDATE verified_skills SET
                            success_count = success_count + 1,
                            consecutive_failures = 0,
                            last_verified_at = datetime('now')
                        WHERE skill_id = @id
                        """;
                }
                else
                {
                    // OLD column values are used in the SET expressions, so (consecutive_failures + 1) is the
                    // new streak; retire once it reaches the threshold.
                    cmd.CommandText = """
                        UPDATE verified_skills SET
                            failure_count = failure_count + 1,
                            consecutive_failures = consecutive_failures + 1,
                            retired_at = CASE WHEN consecutive_failures + 1 >= @thr AND retired_at IS NULL
                                THEN datetime('now') ELSE retired_at END,
                            retirement_reason = CASE WHEN consecutive_failures + 1 >= @thr AND retirement_reason IS NULL
                                THEN 'consecutive_replay_failures' ELSE retirement_reason END
                        WHERE skill_id = @id
                        """;
                    cmd.Parameters.AddWithValue("@thr", retireAfterConsecutiveFailures);
                }
                cmd.Parameters.AddWithValue("@id", skillId);
                cmd.ExecuteNonQuery();
            }

            using var read = _conn.CreateCommand();
            read.CommandText = "SELECT success_count, consecutive_failures, retired_at IS NOT NULL FROM verified_skills WHERE skill_id = @id";
            read.Parameters.AddWithValue("@id", skillId);
            using var reader = read.ExecuteReader();
            if (!reader.Read()) return (0, 0, false);
            return (reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2) != 0);
        }
    }

    /// <summary>Count of distinct verified skills banked for a (pharmacy, task) — observability.</summary>
    public int GetVerifiedSkillCountForTask(string pharmacyId, string taskKey)
    {
        lock (_verifiedSkillLock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM verified_skills WHERE pharmacy_id = @ph AND task_key = @t";
            cmd.Parameters.AddWithValue("@ph", pharmacyId);
            cmd.Parameters.AddWithValue("@t", taskKey);
            return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
        }
    }

    public string SavePricingDiscoveryCandidate(string absolutePath)
        => SavePricingDiscoveryCandidate(absolutePath, DateTimeOffset.UtcNow);

    internal string SavePricingDiscoveryCandidate(
        string absolutePath,
        DateTimeOffset createdAt)
    {
        var token = $"pdc_{Guid.NewGuid():N}";
        lock (_pricingCandidateMemoryLock)
        {
            var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(10);
            _pricingDiscoveryCandidates = _pricingDiscoveryCandidates
                .Where(pair => pair.Value.CreatedAt >= cutoff)
                .ToImmutableDictionary()
                .SetItem(token, new PricingDiscoveryCandidate(absolutePath, createdAt));
        }
        return token;
    }

    public string? TryResolvePricingDiscoveryCandidate(string token)
    {
        if (!IsPricingDiscoveryCandidateToken(token))
        {
            return null;
        }

        lock (_pricingCandidateMemoryLock)
        {
            if (!_pricingDiscoveryCandidates.TryGetValue(token, out var candidate))
                return null;
            _pricingDiscoveryCandidates = _pricingDiscoveryCandidates.Remove(token);
            return candidate.CreatedAt >= DateTimeOffset.UtcNow - TimeSpan.FromMinutes(10)
                ? candidate.AbsolutePath
                : null;
        }
    }

    private sealed record PricingDiscoveryCandidate(
        string AbsolutePath,
        DateTimeOffset CreatedAt);

    private static bool IsPricingDiscoveryCandidateToken(string? token)
    {
        if (token is null || token.Length != 36 ||
            !token.StartsWith("pdc_", StringComparison.Ordinal))
            return false;

        for (var index = 4; index < token.Length; index++)
        {
            var character = token[index];
            if (character is not (>= '0' and <= '9') and
                not (>= 'a' and <= 'f'))
                return false;
        }
        return true;
    }
}
