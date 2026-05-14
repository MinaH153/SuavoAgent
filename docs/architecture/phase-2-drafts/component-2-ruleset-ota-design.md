# Phase 2 Component 2 — Ruleset OTA distribution (DRAFT)

Source: `docs/architecture/diagnostic-mesh-queen-first.md` §1 (Phase 2 row) + §5 (Vault for Phase 1, KMS for Phase 3 — LOCKED).

Status: DRAFT — awaits 7-day Queen burn-in completion (2026-05-20) + `/plan-eng-review` + Codex review before code.

## What changes vs what's already shipped

Phase 1 already shipped (PR #76 + #82 — merged):
- `src/SuavoAgent.Diagnostics/RulesetV1.cs` — model with `key_id`, `signed_at`, `signature_alg`=`ECDSA_P256_SHA256`
- `src/SuavoAgent.Diagnostics/Resources/ruleset-v1.json` — embedded ruleset bundle
- `src/SuavoAgent.Diagnostics/Resources/ruleset-signing-key.pub.pem` — embedded public key (separate keypair from cmd-signing-key per spec §8.4)
- `RulesetV1.LoadEmbedded()` — current load path
- `RulesetV1.VerifySignature()` — STUB returning `true` (Authenticode protects the embedded resource in Phase 1)
- `Wire._ruleset` — single in-memory instance, hot-swappable

Phase 2 work:
- A. Cloud-side: signed-ruleset endpoint serving the current ruleset bundle
- B. Agent-side: polling + verification + swap via existing `ConfigSyncWorker` extension (spec mandate)
- C. Replace `RulesetV1.VerifySignature()` stub with real ECDsa.VerifyData call

## A. Cloud-side endpoint contract

**Path**: `GET /api/agent/ruleset/current`
- Auth: agent installation token (existing `IAgentConfigClient` auth pattern)
- Cacheable: ETag based on ruleset version + signed_at; agent sends `If-None-Match`, cloud returns `304` when unchanged
- Rate limit: 1 req / 5 min per agent (matches `ConfigSyncOptions.PollIntervalSeconds`)

**Response shape** (200 OK):
```json
{
  "ruleset": {
    "ruleset_version": "v1.1",
    "ruleset_version_int": 11,
    "key_id": "ruleset-v1-key-2026-05-13",
    "signed_at": "2026-06-15T18:24:00Z",
    "expires_at": "2027-06-15T18:24:00Z",
    "signature_alg": "ECDSA_P256_SHA256",
    "calibration_fingerprints": { ... },
    "candidate_patterns": [ ... ],
    "invariant_catalog": [ ... ]
  },
  "signature": "<base64 ECDSA P-256 sig over canonicalized ruleset JSON>"
}
```

**Round-7 HIGH (Comp 2 chunk 1) — `ruleset_version_int` wire propagation**: the int field is part of the SIGNED bytes (RFC 8785 canonical form includes it). Required propagation:
1. `RulesetV1.cs` model adds `public int RulesetVersionInt { get; set; }` with `[JsonPropertyName("ruleset_version_int")]`.
2. Embedded `ruleset-v1.json` MUST be bumped to include the new field (otherwise Phase 1 boot fails parse on the now-required field). The Phase 2 code PR bumps the embedded resource simultaneously with the model change.
3. `ruleset-v1.schema.json` adds `ruleset_version_int` to `required`.
4. Cloud Edge Function bundle builder reads + emits `ruleset_version_int` from the source-of-truth ruleset store.
5. The JSON canonicalizer signs the field in RFC 8785 order.

**Signing**: cloud Edge Function fetches the ruleset signing private key from supabase Vault on each request (low volume, cache TTL 5min), signs the canonicalized JSON, returns bundle. Per spec §5 — Vault for Phase 1+2, KMS for Phase 3 when per-tenant keys arrive.

**Canonicalization**: **LOCKED to RFC 8785 (JSON Canonicalization Scheme) only.** No custom fallback. RFC 8785 specifies UTF-16 key ordering + ECMAScript number serialization + forbids Unicode normalization — drift from any of these between cloud and agent produces signed-but-rejected bundles (Codex review v1 CRITICAL, Comp 2 chunk A). Use .NET `JCS` library on cloud side; on agent side use a vetted RFC 8785 implementation (the `JsonCanonicalizer` NuGet package or a hand-rolled implementation with the RFC 8785 Appendix B/property-order test vectors as a golden test). Both implementations MUST pass the shared RFC 8785 conformance vectors before code lands.

## B. Agent-side: extend ConfigSyncWorker

**Why extend, not new worker** (spec mandate): "Signed ruleset OTA distribution endpoint opens (rule push to agent via existing `ConfigSyncWorker`)."

**Where** (`src/SuavoAgent.Core/Workers/ConfigSyncWorker.cs`):

After the existing `_client.FetchAsync()` call lands, do a parallel `_rulesetClient.FetchAsync()`. Independent retry / failure paths so a ruleset fetch failure doesn't break the config-override sync (or vice versa).

New types (in `src/SuavoAgent.Core/Cloud/`):
- `IRulesetClient` — `Task<RulesetBundle?> FetchAsync(string? currentVersion, CancellationToken ct)`
- `RulesetBundle` — `{ RulesetV1 Ruleset, byte[] SignatureBytes, string ETag }`
- `RulesetSyncStore` — handles cached-on-disk persistence + atomic file replace (matches `ConfigOverrideStore` pattern)

The worker:
```csharp
try
{
    var current = RulesetSyncStore.GetCurrentVersion();
    var bundle = await _rulesetClient.FetchAsync(current, stoppingToken);
    if (bundle is null) { /* 304 or transient — skip swap */ }
    else if (!_rulesetVerifier.Verify(bundle))
    {
        // Fail-closed per spec §4: keep old ruleset, alarm via mesh.
        _logger.LogError("Ruleset signature FAILED verification — keeping old ruleset. Reason: {reason}", _rulesetVerifier.LastFailureReason);
        Wire.EmitMeshSignal(SignalKind.WireHandlerFailed, "ConfigSyncWorker", "ruleset.signature_verify_failed");
    }
    else
    {
        await RulesetSyncStore.SaveAsync(bundle, stoppingToken);
        Wire.SwapRuleset(bundle.Ruleset);  // publishes a new RulesetRuntime snapshot
    }
}
catch (Exception ex) { /* swallow per ConfigSyncWorker contract */ }
```

**Wire RulesetRuntime snapshot pattern** (Codex review v1 HIGH, Comp 2 chunk B): The current Wire reads `_ruleset`, `_scrubber`, and `_fingerprinter` as three INDEPENDENT fields on signal-emit threads. A lock-protected swap of those three fields does NOT make readers observe a consistent generation — under .NET memory model (ECMA-335 §I.12.6.6-§I.12.6.7) a reader can see new-ruleset + old-scrubber + old-fingerprinter mid-swap. Fix:

```csharp
public sealed record RulesetRuntime(
    RulesetV1 Ruleset,
    PhiScrubber Scrubber,
    FingerprintComputer Fingerprinter);

private static RulesetRuntime _runtime = null!;  // initialized in Initialize()

public static void SwapRuleset(RulesetV1 next)
{
    // Build OFF-LOCK to avoid lock-order hazards with emit paths re-entering Wire.
    var nextRuntime = new RulesetRuntime(
        next,
        new PhiScrubber(next, _options.ScrubberTimeout),
        new FingerprintComputer(next, _options.FingerprintTimeout));
    Volatile.Write(ref _runtime, nextRuntime);   // single publication barrier
    Interlocked.Increment(ref _meshRulesetSwapsTotal);
    // Emit POST-publication — no lock held; safe even if EmitMeshSignal re-enters Wire.
    EmitMeshSignal(SignalKind.RulesetSwapped, "ConfigSyncWorker",
        $"ruleset_version={next.RulesetVersion} key_id={next.KeyId}");
}

// In every Wire dispatcher (DispatchNormal etc.):
public static void DispatchNormal(...)
{
    var rt = Volatile.Read(ref _runtime);     // ONE read per signal
    var scrubbed = rt.Scrubber.Scrub(...);
    var fp       = rt.Fingerprinter.Compute(...);
    // ... use rt.Ruleset only — never re-read _runtime mid-dispatch
}
```

Publication via `Volatile.Write` + reader `Volatile.Read` produces a memory barrier consistent with the .NET memory model; no lock is needed on the read path, eliminating the lock-order risk Codex flagged.

## C. Replace `RulesetV1.VerifySignature()` stub + multi-key trust store

Current stub at line 137 returns `true`. Phase 2 needs both real signature verification AND a `key_id → ECDsa` trust store from day one (Codex review v1 HIGH, Comp 2 chunk C): a single embedded pubkey makes rotation impossible (compromise rotation cannot safely rely on an OTA signed by the compromised old key; normal rotation requires an agent rebuild). Ship multi-key from the start; embed N keys, ruleset bundle's `key_id` selects which.

New file: `src/SuavoAgent.Diagnostics/RulesetSignatureVerifier.cs`:

```csharp
public sealed class RulesetSignatureVerifier
{
    private const string KeyResourcePrefix = "ruleset-signing-key-";
    private const string KeyResourceSuffix = ".pub.pem";
    private readonly IReadOnlyDictionary<string, ECDsa> _trustStore;

    /// <summary>
    /// Eager-load at startup (Codex round-2 MEDIUM, Comp 2 chunk 2): lazy is
    /// wrong for a signature trust boundary. Throws if zero keys load OR if any
    /// PEM import fails.
    ///
    /// Round-3 MEDIUM: the explicit caller is `ConfigSyncWorker.InitializeAsync`.
    /// Round-6 MEDIUM (Comp 2 chunk 1): use an ALLOWLIST of recoverable
    /// exceptions, not a negative filter — negative filters silently swallow
    /// startup-fatal corruption (AccessViolationException, BadImageFormatException,
    /// SecurityException, TypeLoadException) under the embedded-fallback path.
    /// Only the following exception types should be caught + fall back:
    ///   `catch (ArgumentException ex)         // ExtractKeyId malformed`
    ///   `catch (InvalidOperationException ex) // LoadEmbeddedTrustStore eager-fail`
    ///   `catch (CryptographicException ex)    // ImportFromPem malformed`
    ///   `catch (IOException ex)               // resource stream read fail`
    ///   `catch (UnauthorizedAccessException ex) // file ACL`
    ///   `catch (JsonException ex)             // round-7 MED: corrupt cache parse → alarm + fallback`
    /// All other exceptions escape and crash the process — supervisor restart
    /// is the right recovery path for process-corruption signals.
    /// OperationCanceledException / TaskCanceledException intentionally NOT
    /// caught — caller wants cancel to propagate.
    ///
    /// On throw: emit `ruleset.trust_store_load_failed` mesh signal, set
    /// `_rulesetOtaDisabled = true`, log error, and continue with the embedded
    /// Phase 1 ruleset. The worker never retries trust-store load (a failed
    /// load indicates a build problem, not a transient condition); next
    /// agent restart re-attempts.
    /// </summary>
    public static RulesetSignatureVerifier LoadEmbeddedTrustStore()
    {
        // Embedded resources: <namespace>.ruleset-signing-key-<keyId>.pub.pem (1..N keys).
        // Mirrors SignedCommandVerifier's key_id registry, but with a SEPARATE
        // set of keys (spec §8.4: ruleset signing keypair MUST NOT overlap
        // with cmd-signing-key — crypto-domain separation).
        var asm = typeof(RulesetSignatureVerifier).Assembly;
        var dict = new Dictionary<string, ECDsa>(StringComparer.Ordinal);
        foreach (var resName in asm.GetManifestResourceNames()
                                   .Where(n => n.EndsWith(KeyResourceSuffix, StringComparison.Ordinal)
                                            && n.Contains(KeyResourcePrefix, StringComparison.Ordinal)))
        {
            var keyId = ExtractKeyId(resName);  // hyphen-safe — last marker + suffix strip
            using var stream = asm.GetManifestResourceStream(resName)
                ?? throw new InvalidOperationException($"Embedded resource {resName} unexpectedly null");
            using var reader = new StreamReader(stream);
            var pem = reader.ReadToEnd();
            try
            {
                var ecdsa = ECDsa.Create();
                ecdsa.ImportFromPem(pem);
                dict[keyId] = ecdsa;
            }
            catch (Exception ex)
            {
                // Fail-CLOSED on bad PEM — never silently drop a key from the
                // trust store; that creates a verification gap.
                throw new InvalidOperationException(
                    $"Ruleset trust store: failed to import key '{keyId}' from {resName}", ex);
            }
        }
        if (dict.Count == 0)
        {
            // Fail-CLOSED — zero keys = no OTA verification possible.
            throw new InvalidOperationException(
                $"Ruleset trust store contains zero embedded keys (looked for *{KeyResourcePrefix}<keyId>{KeyResourceSuffix}).");
        }
        return new RulesetSignatureVerifier(dict);
    }

    /// <summary>
    /// Extract key_id from a resource name like
    /// "SuavoAgent.Diagnostics.Resources.ruleset-signing-key-ruleset-v1-key-2026-05-13.pub.pem"
    /// → "ruleset-v1-key-2026-05-13". Hyphen-safe — uses LAST occurrence of
    /// prefix marker + suffix strip, not split.
    /// (Codex round-2 MEDIUM, Comp 2 chunk 2: split-based parsing breaks
    /// rotation because key_ids contain hyphens.)
    /// </summary>
    internal static string ExtractKeyId(string resName)
    {
        var lastPrefix = resName.LastIndexOf(KeyResourcePrefix, StringComparison.Ordinal);
        if (lastPrefix < 0)
            throw new ArgumentException($"Resource {resName} missing {KeyResourcePrefix}", nameof(resName));
        var keyIdStart = lastPrefix + KeyResourcePrefix.Length;
        var keyIdEnd   = resName.Length - KeyResourceSuffix.Length;
        if (keyIdEnd <= keyIdStart)
            throw new ArgumentException($"Resource {resName} has empty key_id segment", nameof(resName));
        return resName.Substring(keyIdStart, keyIdEnd - keyIdStart);
    }

    public VerificationResult Verify(RulesetBundle bundle)
    {
        if (bundle.SignatureBytes is null || bundle.SignatureBytes.Length == 0)
            return new(false, "Missing signature");
        if (!_trustStore.TryGetValue(bundle.Ruleset.KeyId, out var key))
            return new(false, $"Unknown key_id: {bundle.Ruleset.KeyId}");
        if (DateTimeOffset.TryParse(bundle.Ruleset.ExpiresAt, out var exp)
            && exp < DateTimeOffset.UtcNow)
            return new(false, "Ruleset expired");
        var canonicalJson = JsonCanonicalizer.Canonicalize(bundle.Ruleset);  // RFC 8785
        var data = Encoding.UTF8.GetBytes(canonicalJson);
        return key.VerifyData(data, bundle.SignatureBytes, HashAlgorithmName.SHA256)
            ? new(true, null)
            : new(false, "Signature mismatch");
    }
}
```

The Phase 1 `RulesetV1.VerifySignature()` method is moved here (it's now a stub-removal + redirect). `RulesetV1` stays a pure data model; `RulesetSignatureVerifier` is testable independently with valid/invalid/expired/key-mismatch test vectors.

**Key rotation policy** (Codex review v1 MEDIUM, Comp 2 chunk C — framing correction): Vault supports `ecdsa-p256` key versioning; rotation cadence is **MKM operational policy**, not derived from Vault best practices. Default: annual rotation OR immediate on suspected compromise. Vault stores/signs with the private key version; agent trust is controlled by the embedded multi-key public trust store. Adding a new key_id to the agent trust store requires a normal agent OTA release; emergency rotation can pre-stage a future key_id in the trust store before the cloud starts signing with it.

## D. On-disk persistence — RulesetSyncStore atomic replace

(Codex review v1 MEDIUM, Comp 2 chunk B): `File.Move(temp, final)` only suits FIRST install when no final file exists. For subsequent ruleset updates, use Windows `ReplaceFile` semantics via .NET `File.Replace(temp, final, backup)`, which preserves name identity (open file handles continue working) and gives us a recoverable backup. Sequence:

```csharp
public async Task SaveAsync(RulesetBundle bundle, CancellationToken ct)
{
    var finalPath  = Path.Combine(_dir, "ruleset-current.json");
    var tempPath   = Path.Combine(_dir, $"ruleset-{Guid.NewGuid():N}.tmp");
    var backupPath = Path.Combine(_dir, "ruleset-previous.json");

    // Write temp in the SAME directory as final so File.Replace is atomic
    // (cross-volume moves degrade to copy+delete and lose atomicity).
    await using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
    {
        await JsonSerializer.SerializeAsync(fs, bundle, ct);
        await fs.FlushAsync(ct);
        fs.Flush(flushToDisk: true);  // fsync — survive power loss
    }

    if (File.Exists(finalPath))
        File.Replace(tempPath, finalPath, backupPath);  // atomic on NTFS
    else
        File.Move(tempPath, finalPath);
}
```

Backup semantic clarification (Codex round-2 LOW, Comp 2 chunk 3): `ruleset-previous.json` is **single-slot rollback state** — each successful `File.Replace` overwrites the prior backup. We intentionally don't keep a history; the embedded ruleset is the always-available fallback if both `current` and `previous` are corrupt.

**Startup orphan-temp sweep** (Codex round-2 LOW, Comp 2 chunk 3): a partial-write crash mid-`File.Replace` can leave `ruleset-*.tmp` files in the directory. `RulesetSyncStore.InitializeAsync()` runs once at startup and deletes any `ruleset-*.tmp` older than 1 hour before the first fetch attempt; younger temps may be in-flight from a sibling process and are left alone.

## Failure modes (must be tested)

1. **Network unreachable**: keep cached ruleset, no alarm (transient).
2. **HTTP 4xx (auth fail)**: keep cached, alarm (agent token invalid).
3. **HTTP 5xx (cloud down)**: keep cached, no alarm (transient).
4. **Signature INVALID**: keep cached, ALARM (potential tampering or key rotation issue).
5. **Signature key mismatch (cloud signed with key_id X, agent has Y)**: keep cached, ALARM, log key_id mismatch.
6. **Ruleset expired (expires_at in past)**: keep cached, ALARM (cloud-side ruleset rotation failure).
7. **Disk write fail**: in-memory swap still happens; next restart loads embedded; ALARM.

## Open questions for Codex re-review (FOCUSED chunks)

Chunk A (Cloud endpoint): canonical JSON algorithm choice (RFC 8785 vs custom); should the endpoint also stream ruleset.schema.json for forward-compat? Vault secret-rotation cadence?

Chunk B (Agent extension): should `RulesetSyncStore` use atomic-replace via `File.Move` over `File.Replace`? Concurrent reader/writer safety since `Wire.SwapRuleset` runs on the ConfigSyncWorker thread while signal-emission paths read `_ruleset` on whatever thread caught the exception?

Chunk C (Signature verification): should `LoadEmbeddedPublicKey` also support a multi-key trust store (for key rotation)? Spec §8.4 said separate keypair from cmd-signing-key — confirm Phase 2 doesn't accidentally merge them.

## Test coverage

(Codex round-2 MEDIUM, Comp 2 chunk 4 — fill the 7-failure-mode gap with a table-driven matrix; add corruption-after-flush + key_id mismatch + concurrent-swap stress.)

**Unit tests**
- `RulesetSignatureVerifier` valid signature → PASS
- `RulesetSignatureVerifier` invalid signature → FAIL with `"Signature mismatch"`
- `RulesetSignatureVerifier` expired ruleset (ExpiresAt < UtcNow) → FAIL with `"Ruleset expired"`
- `RulesetSignatureVerifier` unknown key_id (cloud signed with `B`, agent trust store has only `A`) → FAIL with `"Unknown key_id: B"`
- `RulesetSignatureVerifier.ExtractKeyId` golden test: `"...ruleset-signing-key-ruleset-v1-key-2026-05-13.pub.pem"` → `"ruleset-v1-key-2026-05-13"` (hyphen-safe)
- `RulesetSignatureVerifier.LoadEmbeddedTrustStore` with zero embedded keys → throws `InvalidOperationException`
- `RulesetSignatureVerifier.LoadEmbeddedTrustStore` with corrupt PEM → throws `InvalidOperationException` (fail-closed, doesn't silently drop)
- `RulesetSyncStore.SaveAsync` first install (no `current` exists) → `File.Move` path executes
- `RulesetSyncStore.SaveAsync` subsequent update → `File.Replace` with backup
- `RulesetSyncStore` startup orphan-temp sweep → 2hr-old `ruleset-*.tmp` deleted, 5min-old preserved

**Startup / cache-state tests** (Codex round-3 HIGH + MED + round-4 MEDs):
- Fresh install, no `ruleset-current.json` exists → agent loads embedded ruleset, no OTA swap, no alarm. Assert `Wire._runtime.Ruleset.RulesetVersion` equals embedded version.
- Boot with valid `ruleset-current.json` signed by trusted key_id → cache loaded, swap to cached ruleset, no alarm.
- **Boot with `ruleset-current.json` signed by ROTATED-OUT key_id** (round-3 HIGH): cached bundle's key_id is no longer in trust store. Agent rejects, alarms `ruleset.cached_key_rotated_out`, records the SHA-256 hash of the rejected file (round-4 MEDIUM: prevents re-alarming on every poll), falls back to embedded.
- Boot with corrupt `ruleset-current.json` (random bytes) → parse fails, alarms, falls back to embedded.
- Boot with valid cache + corrupt embedded resource → cache loads successfully, no alarm. (Defends against accidental embedded-resource damage during build.)
- **Boot with valid cache + valid embedded, embedded ruleset_version_int NEWER than cached** (round-4 MEDIUM + round-6 HIGH): loader prefers embedded (higher monotonic `ruleset_version_int`). Cache load happens after embedded baseline; cache is adopted ONLY on strict greater-than. **Round-5 LOW: equal `ruleset_version_int` tie-break — prefer embedded, do NOT swap to cache.** Cache wins exclusively on `cache.ruleset_version_int > embedded.ruleset_version_int`.

  **Round-6 HIGH (Comp 2 chunk 4 — the round-5 fix was UNSAFE under string compare):** `RulesetVersion` is a STRING (e.g., `"v1.10"`, `"v1.9"`) — lexicographic compare would order `"v1.10" < "v1.9"` and let a stale cache beat a newer embedded. **`RulesetV1` MUST add a monotonic `ruleset_version_int : int` field** signed alongside the rest of the bundle; the string `ruleset_version` becomes display-only. All cache-vs-embedded comparisons use the integer. Required tests: `v1.9 vs v1.10`, equal version, cache older than embedded.

  This handles coordinated cloud+agent releases where cache might lag.

  **Round-7 MEDIUM (Comp 2 — rollback semantics)**: cache-newer-wins is the steady-state rule, but binary rollback (agent self-updates to an OLDER build with a lower `embedded.ruleset_version_int`) leaves the cached newer ruleset adopted on next boot. Policy: **cache freshness wins; rollback is OS/agent-supervisor responsibility, not ruleset-layer**. If an emergency rollback must also revert ruleset, the rollback procedure must either (a) delete `ruleset-current.json` from disk OR (b) write a `ruleset-rollback-epoch.json` sentinel that the loader honors as `max_allowed_ruleset_version_int` for one boot. Document this in the rollback runbook (`docs/runbooks/mesh-rollback.md`); not enforced in code by this draft.

**Failure-mode integration tests** (table-driven against `ConfigSyncWorker` + fake `IRulesetClient`)

| Scenario | Expected outcome |
|---|---|
| Network unreachable | keep cached, no alarm, ConsecutiveFailures++ |
| HTTP 401 (auth fail) | keep cached, alarm `ruleset.auth_failed` |
| HTTP 500 (cloud down) | keep cached, no alarm, transient |
| Signature INVALID | keep cached, alarm `ruleset.signature_verify_failed` |
| Signature key_id mismatch (cloud=B, agent has A only) | keep cached, alarm `ruleset.key_id_unknown` with key_id |
| Ruleset expired | keep cached, alarm `ruleset.expired` |
| Disk write fail (read-only FS) | in-memory swap STILL happens; alarm `ruleset.disk_write_failed`; next restart loads embedded |
| Successful fetch + verify + write | swap happens, mesh.ruleset_version_swaps_total++ |
| Re-fetch with current_version unchanged (304) | no swap, no alarm |

**Corruption-after-flush test**: write a valid bundle to `ruleset-current.json`, corrupt 1 byte after `Flush(true)`, restart agent → assert verifier rejects, alarms, falls back to embedded.

**Concurrent-swap stress test** (round-4 MED — generation-tag assertion + CI tagging + round-5 MED acceptance criteria):
- Each `PhiScrubber` and `FingerprintComputer` instance must expose `RulesetVersion` (`internal` visibility for test access). Without this, the test has nothing to compare across generations.
- Dispatch **≥10,000** `Wire.SwapRuleset(A)` and **≥10,000** `Wire.SwapRuleset(B)` interleaved across N threads; dispatcher reads `_runtime` continuously across ≥4 reader threads → for every read, assert `rt.Ruleset.RulesetVersion == rt.Scrubber.RulesetVersion == rt.Fingerprinter.RulesetVersion` (no mixed generation). Run for a duration target of ≥30s wall-clock to amortize JIT warmup.
- **CI acceptance criteria** (round-5 MED + round-6 LOW — workflow file is a deliverable + CODEOWNERS-protected):
   - Nightly: `.github/workflows/mesh-stress-nightly.yml` lands in the Phase 2 code PR (NOT a follow-up; Comp 2 ship is blocked on this file existing). Owner = `@MinaH153` (no team handle yet — round-7 LOW: replace `@<oncall-team>` placeholder; when a `@SuavoLLC/mesh-oncall` team is created, add it here). CODEOWNERS entry: `/.github/workflows/mesh-stress-*.yml @MinaH153`. Runs `dotnet test --filter "Category=Stress"` for the 10k×30s variant. Failure notifications → `#mesh-alerts` Slack + page on 2 consecutive nightly failures.
   - PR CI smoke: 200 iterations of the same `[Fact]` (NO `[Trait("Category","Stress")]`), with `[Trait("Timing","Fast")]`. **Wall-clock ceiling calibrated empirically** (round-7 LOW): Phase 2 code PR runs the test 50× on `ubuntu-latest` runners (in a calibration job), captures p50 / p95 / p99 wall-clock, and sets the ceiling to `p99 × 1.5`. Don't guess 5s; measure. The ceiling is recorded in a comment alongside the test and revisited if CI runners change.
- Single-publisher (only ConfigSyncWorker calls SwapRuleset) is a separate contract test.

**Property test**: PhiScrubber + FingerprintComputer outputs unchanged for Bug 22/23/24 calibration vectors after `Wire.SwapRuleset` to an identical ruleset (verifies snapshot construction doesn't drift state).
