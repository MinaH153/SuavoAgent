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

**Signing**: cloud Edge Function fetches the ruleset signing private key from supabase Vault on each request (low volume, cache TTL 5min), signs the canonicalized JSON, returns bundle. Per spec §5 — Vault for Phase 1+2, KMS for Phase 3 when per-tenant keys arrive.

**Canonicalization**: agent and cloud must agree on JSON canonicalization. Use RFC 8785 (JSON Canonicalization Scheme) OR a custom "minimal canonical": object keys lexicographically sorted, no insignificant whitespace, UTF-8 NFC, numbers as decimals. Codex re-review must pick one; recommend RFC 8785 for tooling availability.

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
        Wire.SwapRuleset(bundle.Ruleset);  // new API on Wire
    }
}
catch (Exception ex) { /* swallow per ConfigSyncWorker contract */ }
```

New `Wire.SwapRuleset(RulesetV1)` API:
- Replaces `Wire._ruleset` under a lock
- Increments `mesh.ruleset_version_swaps_total` counter for observability
- Rebuilds `_scrubber` and `_fingerprinter` (both take `_ruleset` in their constructors)

## C. Replace `RulesetV1.VerifySignature()` stub

Current stub at line 137 returns `true`. Phase 2 implementation:

```csharp
public bool VerifySignature(byte[] signatureBytes)
{
    if (signatureBytes is null || signatureBytes.Length == 0) return false;
    var pubKey = LoadEmbeddedPublicKey();  // ruleset-signing-key.pub.pem
    using var ecdsa = ECDsa.Create();
    ecdsa.ImportFromPem(pubKey);
    var canonicalJson = CanonicalizeForSigning(this);  // RFC 8785
    var data = Encoding.UTF8.GetBytes(canonicalJson);
    return ecdsa.VerifyData(data, signatureBytes, HashAlgorithmName.SHA256);
}
```

Move this method to a new helper class `RulesetSignatureVerifier` so it's testable independently of `RulesetV1` (which is a data model).

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

- Unit: `RulesetSignatureVerifier` with valid/invalid/expired signatures (test vectors)
- Unit: `RulesetSyncStore` atomic-replace + load + corruption recovery
- Integration: `ConfigSyncWorker` + fake `IRulesetClient` → assert correct swap on success, no swap on verify fail
- Property: PhiScrubber + FingerprintComputer behavior unchanged after `Wire.SwapRuleset` (golden test using calibration fingerprints from Phase 1)
