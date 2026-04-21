# SuavoAgent Self-Healing — Action Grammar v1 (v0.1)

> This is the action grammar contract. The typed verbs the agent exposes, how
> they are declared, signed, versioned, rolled out, and verified. Every code
> path that causes state change on a pharmacy workstation MUST go through a
> verb defined here.

**Locked date:** 2026-04-21
**Status:** v0.1 draft
**Depends on:** `invariants.md`, `audit-schema.md`, `key-custody.md`

---

## Why typed verbs (vs raw shell)

Every surveyed LLM-agent system that allowed raw-shell execution (Cline YOLO,
OpenHands V0 sandbox-optional, Autogen command dispatch) has had at least one
public incident where an LLM hallucinated a destructive command and executed
it. Every system that went typed-verb-only (LaVague before abandonment,
Anthropic's computer-use reference, Moveworks Compound Actions, SentinelOne
RemoteOps allowlist) has had ZERO published hallucination-driven execution
incidents.

This is not a coincidence. Typed verbs are the single strongest safety
mechanism in this architecture.

MCP security arxiv (2511.20920) makes it explicit: *"`get_revenue_for_month(month, year)` beats `execute_sql(query)`. Deterministic, auditable."*

We take this as canonical.

---

## Verb schema

Every verb is a C# class implementing `IVerb` with a static declaration
metadata block. This metadata is what the cloud dispatcher reads, what the
policy engine evaluates, what the audit trail records, what the plan-review UI
renders.

```csharp
public interface IVerb
{
    VerbMetadata Metadata { get; }
    VerbPreconditionResult CheckPreconditions(VerbContext ctx);
    VerbExecutionResult Execute(VerbContext ctx);
    VerbRollbackEnvelope CaptureRollback(VerbContext ctx);
    VerbPostconditionResult VerifyPostconditions(VerbContext ctx);
}

public record VerbMetadata(
    string Name,                   // e.g., "restart_service"
    string Version,                // semver, strict
    string Description,            // human-readable
    VerbRiskTier RiskTier,         // LOW | MED | HIGH | UNKNOWN
    VerbBaaScope BaaScope,         // None | AgentBaa | BaaAmendment(id)
    bool IsMutation,               // true if changes on-box state
    bool IsDestructive,            // true if rollback is lossy (e.g., rotate_api_key)
    TimeSpan MaxExecutionTime,     // watchdog timeout
    VerbParameterSchema Params,    // typed input contract
    VerbOutputSchema Output,       // typed output contract
    VerbBlastRadius BlastRadius,   // expected impact envelope (see §Blast Radius)
    IReadOnlyList<string> RequiresVerbs, // other verbs that must succeed before this one
    IReadOnlyList<string> ConflictingVerbs // verbs that cannot run concurrently
);
```

Example:

```csharp
public class RestartServiceVerb : IVerb
{
    public VerbMetadata Metadata => new(
        Name: "restart_service",
        Version: "1.0.0",
        Description: "Restart a named Windows service via sc.exe",
        RiskTier: VerbRiskTier.LOW,
        BaaScope: VerbBaaScope.None,
        IsMutation: true,
        IsDestructive: false,
        MaxExecutionTime: TimeSpan.FromSeconds(90),
        Params: new(
            Required: [new("service_name", typeof(string), "enum:SuavoAgent.Core|SuavoAgent.Broker|SuavoAgent.Watchdog")]
        ),
        Output: new(
            Fields: [new("final_state", typeof(ServiceState)), new("duration_ms", typeof(long))]
        ),
        BlastRadius: new(
            ExpectedDollarsImpact: 0,
            PhiRecordsExposed: 0,
            DowntimeSeconds: 90,
            RecoverableWithinSeconds: 300
        ),
        RequiresVerbs: [],
        ConflictingVerbs: ["restart_service", "apply_config_override"]
    );

    // ... precondition / execute / rollback / postcondition impls ...
}
```

---

## Versioning

### Semver applied strictly

- **MAJOR** version bump: breaking change to parameter schema, output schema,
  risk tier, or BAA scope. Agents running prior MAJOR version REFUSE to
  execute. Cloud refuses to dispatch to agents on prior MAJOR.
- **MINOR** version bump: new optional parameter, new optional output field,
  expanded enum values (backward compatible). Agents on prior MINOR still
  work; cloud can dispatch to either.
- **PATCH** version bump: bug fix, no interface change. Agents transparent.

### Fail-closed on schema mismatch

This is THE CrowdStrike lesson (July 2024 Channel File 291). A content file
with 21 fields vs code expecting 20 fields killed 8.5M Windows hosts, $1.94B
in healthcare losses. Root cause: no schema version check, no bounds check,
no canary.

We do not allow schema mismatch to EVER execute. Fail closed, always.

Enforcement on the agent side:

```csharp
public class VerbDispatcher
{
    public VerbDispatchResult Dispatch(SignedVerbInvocation invocation)
    {
        // 1. Schema version check (FIRST THING, before signature check)
        var expectedSchemaHash = _registry.GetSchemaHash(invocation.VerbName, invocation.VerbVersion);
        if (expectedSchemaHash != invocation.SchemaHash)
            return VerbDispatchResult.Reject("schema_version_mismatch",
                expected: expectedSchemaHash, got: invocation.SchemaHash);

        // 2. Signature verification
        if (!_signer.Verify(invocation.Bytes, invocation.Signature))
            return VerbDispatchResult.Reject("invalid_signature");

        // 3. Fence ID check
        if (invocation.FenceId != _currentFence.Id)
            return VerbDispatchResult.Reject("fence_mismatch",
                expected: _currentFence.Id, got: invocation.FenceId);

        // 4. Parameter schema validation (typed)
        var validation = _registry.ValidateParameters(invocation.VerbName, invocation.VerbVersion, invocation.Parameters);
        if (!validation.IsValid)
            return VerbDispatchResult.Reject("parameter_validation_failed", validation.Errors);

        // 5. Precondition check
        var verb = _registry.Load(invocation.VerbName, invocation.VerbVersion);
        var precond = verb.CheckPreconditions(context);
        if (!precond.Satisfied)
            return VerbDispatchResult.Reject("precondition_failed", precond.Reason);

        // 6. Capture rollback envelope
        var envelope = verb.CaptureRollback(context);
        _auditChain.Append("verb.rollback_captured", envelope);

        // 7. Execute
        var result = verb.Execute(context);

        // 8. Verify postconditions
        var postcond = verb.VerifyPostconditions(context);
        if (!postcond.Satisfied)
        {
            _auditChain.Append("verb.postcondition_failed", postcond);
            _rollbackExecutor.Execute(envelope);
            return VerbDispatchResult.Rollback(postcond.Reason);
        }

        // 9. Commit
        _auditChain.Append("verb.executed", result);
        return VerbDispatchResult.Success(result);
    }
}
```

Every step above emits an audit event. No step is optional. No step runs
without the prior step succeeding.

---

## Risk tiers

Every verb declares one of four risk tiers. Risk tier drives the approval
gate.

| Tier | Definition | Default approval path |
|---|---|---|
| **LOW** | Bounded blast radius, fully rollback-able, no PHI exposure risk (e.g., `restart_service`, `invoke_schema_canary`) | Can be auto-approved per operator grant + autonomy ladder. Default for new pharmacies: still requires approval. |
| **MED** | Changes on-box state, rollback possible but may require manual intervention (e.g., `apply_config_override`, `rotate_api_key`) | Operator approval always required. Time-boxed consent (30 min default). |
| **HIGH** | Touches PHI-adjacent data, destructive without full rollback, or affects pharmacy Rx workflow (e.g., `pioneerrx_writeback_rx_delivery`) | Operator approval required + MFA challenge + 2-person approval for the first 30 days at any pharmacy. |
| **UNKNOWN** | New verb type or incomplete risk analysis | Structurally rejected until explicitly classified by Joshua + Security Officer. |

**OpenHands V1 SecurityAnalyzer pattern:** the LLM dynamically upgrades risk
tier at invocation time based on context (e.g., "this restart_service call
targets a service that hasn't been observed healthy in 72h → promote to MED").
This dynamic upgrade is honored but NEVER downgrades — a LOW-declared verb
can be promoted to MED at runtime but never demoted.

---

## Blast radius declaration

Every verb declares its expected impact envelope. Cedar policy + fleet
operator dashboard use this for budget-based execution gating (Codex creative
idea #8: Blast-Radius Economics Engine).

```csharp
public record VerbBlastRadius(
    decimal ExpectedDollarsImpact,    // cost if this verb goes wrong
    int PhiRecordsExposed,            // upper bound on PHI records that could be exposed
    int DowntimeSeconds,              // expected pharmacy downtime from this verb
    int RecoverableWithinSeconds,     // time to roll back if failure detected
    string Justification              // human-readable reasoning for these numbers
);
```

If estimated blast radius exceeds tenant threshold, autonomous execution is
blocked; verb must go to human approval. Thresholds set per-pharmacy via Cedar
policy.

---

## BAA scope binding

Every verb carries a BAA scope tag (§I.2 of invariants). Enforcement:

```csharp
public VerbBaaScope BaaScope { get; init; }

public bool IsAllowedForPharmacy(Pharmacy p)
{
    return this.BaaScope switch
    {
        VerbBaaScope.None => true,
        VerbBaaScope.AgentBaa => p.HasActiveAgentBaa,
        VerbBaaScope.BaaAmendment(var id) => p.HasActiveBaaAmendment(id),
        _ => throw new InvalidVerbScopeException()
    };
}
```

**Structural rejection** at the verb-registry level. A verb that requires a
BAA amendment the pharmacy doesn't hold cannot be dispatched to that pharmacy.
The policy engine does not even need to evaluate it.

---

## Rollback envelopes

### Rollback is a first-class verb output

Every mutating verb MUST produce a rollback envelope at `CaptureRollback()`
time, BEFORE execution. The envelope is:

```csharp
public record VerbRollbackEnvelope(
    string VerbInvocationId,
    string InverseActionType,
    Dictionary<string, object> PreState,     // snapshot of what's being mutated
    Func<VerbContext, Task> InverseFn,       // code that reverts
    TimeSpan MaxInverseDuration,
    string Evidence                          // hash or reference to pre-state evidence
);
```

The envelope is appended to the audit chain as a `verb.rollback_captured`
event BEFORE execution. If anything goes wrong mid-execution or if postcondition
verification fails, the `InverseFn` runs and its completion is audited.

### Rollback is provable

Every rollback envelope includes an `Evidence` field — typically a SHA-256 of
the pre-state (registry key value, file content, SQL snapshot). After inverse
runs, verifier can re-hash the current state and prove rollback was
successful.

**Codex creative idea #3 (Deterministic Replay DSL):** rollback envelopes are
composable. A plan with 5 verbs has 5 envelopes; plan-level rollback executes
them in reverse order. This is Saga pattern from Temporal — not invented here.

### Rollback time budget

Every envelope declares `MaxInverseDuration`. If rollback exceeds, escalate to
human operator with an `invariant.violated` event. Rollback that can't complete
in-window is a product-level failure we need to fix.

---

## Verb registry

Agent-side:
- `SuavoAgent.Verbs/` project (Phase D deliverable)
- `VerbRegistry.cs` — loads verb bundle at startup, validates signatures
- Bundle = signed ZIP with one `.dll` per verb + `manifest.json`
- Manifest declares verb names, versions, schema hashes

Cloud-side:
- `verb_catalog` Postgres table, per-pharmacy-per-version deployment
- Cedar policy attached to each (pharmacy, verb_name) pair
- Canary rollout manifest tracks 1% → 5% → 25% → 100% progression

---

## Canary rollout (mandatory for every verb change)

**Rule: no verb version goes to >N pharmacies without soak at prior tier.**

Tier progression:
1. **Dev tier (1 pharmacy)**: Joshua's Bakersfield test box. 24-hour soak. All postconditions pass.
2. **Pilot tier (1 pharmacy — Nadim or equivalent)**: 48-hour soak. No rollbacks. No operator complaints.
3. **5% (next 2-3 pharmacies)**: 72-hour soak. Monitoring for error rate > baseline.
4. **25% (larger cohort)**: 7-day soak.
5. **100% (full fleet)**.

**Auto-halt on anomaly:** error rate > 2× baseline during any tier → rollout
pauses, Security Officer alerted, investigation required before unpausing.

**Halt-all:** if error rate crosses critical threshold at ANY tier, entire
rollout is reverted across all tiers within 15 minutes via `emergency_revert`
verb-bundle delivery.

This is THE CrowdStrike lesson applied rigorously. No exceptions. No "but
it's just a patch" excuses.

---

## Universal verbs v1 (initial 5)

The Phase D launch set. Chosen for: bounded blast radius, fully rollback-able,
broad applicability across pharmacies, already-proven safety in the Watchdog
already deployed.

1. **`restart_service`** v1.0.0 — LOW risk
   - Params: `service_name` (enum of {Core, Broker, Watchdog})
   - Preconditions: service is registered, currently in non-RUNNING state >5min grace
   - Rollback: none needed (idempotent; service is either running or not)
   - Postcondition: service reaches RUNNING within 90s

2. **`rotate_api_key`** v1.0.0 — MED risk
   - Params: none
   - Preconditions: current key is >60 days old OR compromise suspected
   - Rollback: restore previous key from cloud (24h grace window)
   - Postcondition: next heartbeat signs successfully with new key

3. **`apply_config_override`** v1.0.0 — MED risk
   - Params: `key_path` (structured, no free-form), `new_value` (typed)
   - Preconditions: key exists in current config, new_value passes schema
   - Rollback: write old value back (pre-state captured in envelope)
   - Postcondition: config file hash changes to expected SHA-256

4. **`invoke_schema_canary`** v1.0.0 — LOW risk
   - Params: none
   - Preconditions: PMS is running
   - Rollback: none needed (read-only operation)
   - Postcondition: canary completes within 60s, emits result event

5. **`rerun_bootstrap_probe`** v1.0.0 — LOW risk
   - Params: none
   - Preconditions: bootstrap.ps1 available at expected path
   - Rollback: none needed (read-only diagnostic)
   - Postcondition: probe JSON report emitted within 120s

Every one of these has its own full spec doc in
`docs/self-healing/verbs/<verb-name>-v<version>.md` (Phase D deliverables).

---

## Pharmacy-specific verbs (v2, Phase D.2)

Post-universal. Examples:

- **`pioneerrx_query`** (LOW, read-only, AgentBaa)
- **`pioneerrx_click`** (MED, UI automation, AgentBaa)
- **`pioneerrx_writeback_rx_delivery`** (HIGH, DB mutation, requires
  BAA amendment `writeback-v1`)

Each with full spec, canary rollout, rollback envelope. Not shipping in
universal v1.

---

## Verb composition (future, Phase E+F)

Codex creative idea #5 — SmallTalk-style verb algebra.

Example future syntax:

```
reboot_and_probe = restart_service ; invoke_schema_canary ; await_steady_state(timeout=45s)
```

Compositions compile to Temporal workflows (Phase E). Compatibility metadata
on verbs (does this commute with that one? can they run concurrently?) allows
composition engine to reorder safely.

Not in v1 scope. Keep verbs atomic for now. Composition is a Phase F-or-later
feature.

---

## Enforcement points

Every time the word "verb" appears in code, the following must be true:

1. Its metadata is read from a signed, canary-rolled verb bundle
2. Its schema version matches what the dispatcher expects
3. Its signature has been verified
4. Its fence ID matches current kill-switch state
5. Its BAA scope matches the target pharmacy's current BAA state
6. Its preconditions have been checked
7. Its rollback envelope has been captured and audit-logged
8. Its execution is wrapped in a Cedar-policy-approved context
9. Its postconditions have been verified
10. Every step emits an audit event

Violating ANY of these is a CRITICAL invariant violation (§I.11).

---

## CI enforcement

Every PR that touches `src/SuavoAgent.Verbs/` must:
- Pass schema hash stability test (PATCH versions can't change schema)
- Pass version-monotonicity test (can't decrement)
- Declare explicit risk tier + blast radius (no UNKNOWN in merged PRs)
- Include a `<verb-name>-v<version>.md` spec doc
- Include unit tests covering: valid invocation, invalid params, precondition
  failure, execution failure, rollback execution, postcondition failure

These checks are the pre-merge gates. PRs failing any are blocked by CI.

---

## Action grammar evolution path

- **v0.1 (today, 2026-04-21)**: this doc, draft
- **v1.0 (post-Nadim pilot, ~2026-04-28)**: lock after Codex review
- **v1.1 (Phase D, ~2026-08)**: initial 5 universal verbs shipped
- **v1.2 (Phase D.2, ~2026-09)**: pharmacy-specific verbs added
- **v2.0 (Phase E+F, ~2026-11)**: verb composition syntax + Temporal integration
- **v2.1+ (Phase G+)**: partner-contributed verbs via signed SDK

Each version bump follows the same pattern:
- Canary rollout on 1 pharmacy
- 48-hour soak
- 5% → 25% → 100% with auto-halt gates
- Prior version remains in accepted-schemas list for 30 days

---

## Change log

- **2026-04-21 v0.1** — Initial draft. Locks to v1.0 after Nadim pilot + Codex review.
