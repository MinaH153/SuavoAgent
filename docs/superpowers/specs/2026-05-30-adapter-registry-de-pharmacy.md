# W4a — Adapter Config Registry + PHI Invariant (Design Spec)

> **Status:** Codex R1 APPROVE-WITH-CHANGES + R2 REJECT (engine seam) → **scope deliberately reduced** to honest config-registry (see §9). Ready for TDD.
> **Branch:** `feat/w4a-adapter-registry`
> **Program:** W4 "Truly-smart" agent, chunk P0a (`~/.claude/plans/distributed-churning-spark.md`)
> **Vision:** `[[suavoagent-product-vision-2026-05-01]]`, `[[suavoagent-universal-vision]]` — pharmacy is the beachhead, **not** the product.

## HIPAA invariants (non-negotiable — enforced as compile/test errors)

1. **Every registered adapter MUST carry a non-empty PHI policy.** No PHI guard = HIPAA hole → registry ctor throws; a test asserts it over every registered config. `AdapterConfig` only enters through the registry (no bypass), so the guarantee holds without a Roslyn analyzer.
2. **PHI-column classification is preserved bit-for-bit.** `PioneerRxConstants.IsPhiColumn` → `PhiPolicy.IsPhiColumn` is a pure port; a characterization test pins old==new across the corpus, explicitly covering `OrdinalIgnoreCase` blocklist match **and** substring pattern match.
3. **No PHI enters prompts/logs/telemetry/cloud keys.** This chunk touches neither reasoning prompts (W4b) nor what crosses the cloud boundary. `Pms`/`schemaSignature`/catalog are non-PHI metadata.
4. **Zero behavior change for PioneerRx.** The PioneerRx config equals today's literals; the worker still uses today's `PioneerRxSqlEngine`. Behavior-preserving by construction (pinned by a payload-snapshot test).

---

## 1. Goal

Centralize the scattered PioneerRx configuration (process identity, status vocabulary, **PHI policy**, default catalog, `Pms`/`schemaSignature` labels) behind an `IAdapterRegistry`, make the PHI policy a **first-class enforced invariant**, fix the latent catalog-default bug, and lay the registry skeleton that W3's multi-app positioning copy rests on.

**Explicitly NOT a multi-app detection proof.** The production engine stays PioneerRx (`PioneerRxSqlEngine`); only its *configuration* becomes registry-driven. The 2nd-adapter artifact (`ComputerRxAdapterConfig`) is a **config-registration smoke test**, not a detection proof.

## 2. Confirmed current state (verified by code read, 2026-05-30)

Four partial adapter surfaces exist; this chunk touches only the **config** that feeds the production detection path (`RxDetectionWorker`) and the pricing factory.

| Surface | File | Role |
|---|---|---|
| `IPharmacyReadAdapter` | `Core/Adapters/IPharmacyReadAdapter.cs:13` | `AdapterType` string = `pioneerrx`/`computerrx`/… — the family key we reuse. |
| `ILocalPmsAdapter` | `Contracts/Adapters/ILocalPmsAdapter.cs:6` | Learning path (`LearnedPmsAdapter` via `AdapterGenerator`). Untouched. |
| `IPricingLookupFactory` | `Pricing/PricingJobExecutor.cs:39` | Executor abstracted; only `PioneerRxSqlPricingLookupFactory` baked. |
| **`RxDetectionWorker`** | `Workers/RxDetectionWorker.cs:18` | Production heartbeat; news up `PioneerRxSqlEngine` (`:382`). **Engine stays; config gets injected.** |

**`AdapterType` (string) is the key** — reused from `IPharmacyReadAdapter.cs:15-19`. String, not enum: learned adapters emit arbitrary family names an enum would block.

**Hardcoded config sites to migrate (exact):**
- `PioneerRxConstants.cs` — `ProcessName="PioneerPharmacy"` (`:5`), `DefaultWindowTitle` (`:6`), status names + matchers (`:15-53`), `PhiColumnBlocklist`/`PhiColumnPatterns`/`IsPhiColumn` (`:61-95`).
- `"PioneerPharmacySystem"` catalog — `AgentOptions.cs:149`, `AgentOptions.cs:169`, `RxDetectionWorker.cs:379` (+ writeback CSB `:410`), `PricingJobExecutor.cs:215`.
- `Pms: "PioneerRx"` — `RxDetectionWorker.cs:538`; `schemaSignature = "pioneerrx.sql.metadata.v1"` — `:510`.

## 3. Non-goals / scope boundary (deliberately deferred — see §9 for why)

- **NOT** abstracting the detection engine, the `RxMetadata` record (`StatusGuid` stays), or the writeback type. Codex R2 proved a PMS-neutral contract can't be drawn correctly from one PMS — **deferred to P2 (cross-PMS canonical-schema), to be designed against a real second example or after W4b vision-grounded reasoning lands.** `RxDetectionWorker` keeps `new PioneerRxSqlEngine`; we only change where its *config* comes from.
- **NOT** vision-grounded reasoning (W4b) — the actual app-agnostic unlock.
- **NOT** `AdapterGenerator`→registry emission (P0b fast-follow; `AdapterConfig` is shape-compatible).
- **NO** agent-behavior change on a real box until W1 v3.15.2/.3 is field-confirmed; this chunk is behavior-preserving regardless.

## 4. Design

### 4.1 Registry + config (`src/SuavoAgent.Contracts/Adapters/`)

```csharp
public interface IAdapterRegistry
{
    IReadOnlyCollection<AdapterConfig> All { get; }
    AdapterConfig Default { get; }                       // back-compat: "pioneerrx"
    AdapterConfig Resolve(string adapterType);           // normalizes key; throws AdapterNotRegisteredException
    bool TryResolve(string adapterType, out AdapterConfig config);
}

public sealed record AdapterConfig
{
    public required string AdapterType { get; init; }    // normalized lower/trim — join key
    public required string DisplayName { get; init; }    // "PioneerRx" — the Pms provenance label (SerializeRxBatch Pms field)
    public required ProcessIdentity Process { get; init; }
    public required PhiPolicy Phi { get; init; }
    public required SqlProfile Sql { get; init; }
}

public sealed record ProcessIdentity(string ProcessName, string DefaultWindowTitle);
public sealed record SqlProfile(string DefaultCatalog, string SchemaSignature);
// StatusVocabulary DROPPED — see §9 encapsulation finding (PioneerRx status vocab stays internal to the adapter).
```

`AdapterRegistry` ctor: **normalizes** each key (`Trim().ToLowerInvariant()`), **throws on duplicate** keys, **re-checks** every `Phi` is non-empty (invariant #1). `Resolve` normalizes the lookup key identically.

### 4.2 PHI policy + invariant (`Contracts/Adapters/PhiPolicy.cs`)

```csharp
public sealed record PhiPolicy
{
    public required IReadOnlySet<string> ColumnBlocklist { get; init; }   // OrdinalIgnoreCase set
    public required IReadOnlyList<string> ColumnPatterns { get; init; }
    public bool IsPhiColumn(string columnName);                           // EXACT port of PioneerRxConstants.IsPhiColumn
    public static PhiPolicy Create(IReadOnlySet<string> blocklist, IReadOnlyList<string> patterns)
        => (blocklist.Count == 0 && patterns.Count == 0)
            ? throw new ArgumentException("PHI policy must define ≥1 blocklist column or pattern")
            : new() { ColumnBlocklist = blocklist, ColumnPatterns = patterns };
}
```

### 4.3 PioneerRx config (constants become the data source)

`PioneerRxConstants` stays the single source of the literal values; `PioneerRxAdapterConfig.Create()` (new, `Adapters.PioneerRx`) **reads from** the constants → returns an `AdapterConfig`. Status matchers move to `StatusVocabulary`; `PioneerRxConstants` matcher methods become thin forwarders (deleted once no caller remains — grep gate §8).

### 4.4 `PharmacyConfig` selector + nullable catalog — fixes Codex Q3 (`AgentOptions.cs:165`)

```csharp
public sealed class PharmacyConfig
{
    public string AdapterType { get; set; } = "pioneerrx";   // NEW selector (back-compat default)
    public string? SqlDatabase { get; set; } = null;         // CHANGED from "PioneerPharmacySystem" → null
    // ...existing fields...
}
```

`GetEffectivePharmacies()` (`:149`) drops the `?? "PioneerPharmacySystem"` literal. Catalog resolves **after** adapter selection: `pharmacy.SqlDatabase ?? cfg.Sql.DefaultCatalog`. A `pioneerrx` pharmacy with null `SqlDatabase` → `"PioneerPharmacySystem"` via config (behavior preserved, test 7). **Audit all `SqlDatabase` readers** (worker `:379/:410`, pricing `:215`) to use the post-selection fallback — none may assume non-null.

### 4.5 DI registration (`src/SuavoAgent.Core/Adapters/AdapterRegistration.cs`, from `Program.cs`)

```csharp
public static IServiceCollection AddAdapterRegistry(this IServiceCollection services)
    => services.AddSingleton<IAdapterRegistry>(_ => new AdapterRegistry(
        new[] { PioneerRxAdapterConfig.Create(), ComputerRxAdapterConfig.Create() }, defaultAdapterType: "pioneerrx"));
```

Registering ComputerRx does not select it; selection is per-pharmacy via `PharmacyConfig.AdapterType`.

### 4.6 Consumption — resolve once, config only (fixes Codex Q5)

- `RxDetectionWorker.TryConnectSqlAsync` (`:358-449`): inject `IAdapterRegistry`. **Resolve `cfg` once** per cycle. Keep `new PioneerRxSqlEngine(...)` — but source `database` from `pharmacy.SqlDatabase ?? cfg.Sql.DefaultCatalog`. `SerializeRxBatch` (`:489`, static, pure): thread `cfg.Sql.PmsLabel` + `cfg.Sql.SchemaSignature` as params (replacing `:538`/`:510` literals). Install detector + canary + writeback wiring **unchanged** (PioneerRx-only; engine work is P2).
- `PioneerRxSqlPricingLookupFactory.BuildConnectionString` (`:215`): inject `IAdapterRegistry`; `?? _registry.Default.Sql.DefaultCatalog`.

## 5. Second-adapter artifact (config-registration smoke test — honest framing)

`ComputerRxAdapterConfig.Create()` returns a minimal valid `AdapterConfig` (`AdapterType="computerrx"`, plausible process/status placeholders, a **real** `PhiPolicy`). A test asserts: `registry.Resolve("computerrx")` succeeds, its PHI policy is non-empty, and it doesn't collide with `pioneerrx`. This proves the **registry mechanism** extends to N>1 configs. It does **not** claim ComputerRx can detect — that needs the P2 engine/contract work.

## 6. Behavior-preservation & rollout

PioneerRx config == current literals; worker still uses today's engine → registry-on == today for every PioneerRx pharmacy (payload-snapshot test). ComputerRx config is inert (no pharmacy selects it). Merges to `main` freely (behavior-preserving). No real-box behavior change.

## 7. Test strategy (xUnit — match `tests/SuavoAgent.Core.Tests/` + `.Helper.Tests/`)

1. `AdapterRegistryTests` — Resolve/TryResolve/Default; unknown → throws; **duplicate key → throws**; **normalization** ("PioneerRx"/" pioneerrx " resolve same); empty `PhiPolicy` → ctor throws.
2. `PhiPolicyCharacterizationTests` — across the PioneerRx corpus (blocklist exact + mixed-case + substring positives + negatives), `PhiPolicy.IsPhiColumn == PioneerRxConstants.IsPhiColumn`. Pins invariant #2.
3. `PioneerRxAdapterConfigTests` — config values equal legacy constants (catalog, process, both status lists, window, Pms label, schema signature).
4. `StatusVocabularyTests` — matchers == old `PioneerRxConstants` matchers across the same strings.
5. `RxDetectionWorkerAdapterTests` — registry wired, `SerializeRxBatch` output for a fixed Rx set is **byte-identical** to the pre-change snapshot. Reuse existing fixtures.
6. `PharmacyConfigCatalogTests` — pioneerrx + null `SqlDatabase` → `"PioneerPharmacySystem"`; computerrx + null → ComputerRx catalog (not PioneerRx's). Fixes Q3.
7. `ComputerRxRegistrationTests` — the §5 smoke test.
8. Full `dotnet test` green (net8.0 + win-x64 gated).

## 8. Build sequence (each an independent TDD unit; expand to bite-sized steps via `superpowers:writing-plans`)

1. Contracts: `AdapterConfig` + sub-records + `PhiPolicy` (factory) + `IAdapterRegistry` + `AdapterNotRegisteredException`. Tests 1,2,4.
2. `AdapterRegistry` impl (normalize + dedup + PHI validation). Test 1.
3. `PioneerRxAdapterConfig.Create()` sourced from `PioneerRxConstants`; move matchers to `StatusVocabulary`. Tests 3,4.
4. `PharmacyConfig.AdapterType` + nullable `SqlDatabase`; `GetEffectivePharmacies()` literal removal; audit all `SqlDatabase` readers; `AddAdapterRegistry` DI + `Program.cs`. Test 6 + DI smoke.
5. Migrate `RxDetectionWorker` → resolve-once + registry-sourced catalog + threaded labels (engine construction unchanged). Test 5.
6. Migrate `PioneerRxSqlPricingLookupFactory` catalog → registry. Pricing factory test.
7. `ComputerRxAdapterConfig`. Test 7.
8. Grep gate: no `"PioneerPharmacySystem"`/`"PioneerRx"` literal in `Core/Workers`/`Core/Pricing`; `dotnet test` green.

## 9. Codex review log + scope decision

- **R1 (APPROVE-WITH-CHANGES):** Q2 (string key + normalize/dedup), Q3 (nullable catalog — real bug), Q4 (PHI factory enforcement + characterization), Q5 (thread labels, resolve once) → **all ACCEPTED, in this spec.** Q1 (engine seam) → see R2.
- **R2 (REJECT):** the engine seam was dishonest — `RxMetadata.StatusGuid` + PioneerRx-typed writeback + a no-PMS stub leak/fake PioneerRx semantics; a real neutral contract can't be drawn from one PMS.
- **DECISION (Joshua delegated; expert call):** **Override Codex's "build neutral contracts now."** R2 proves the abstraction can't be drawn cleanly from N=1 — which is the signal it's *premature*, not that we should push harder. No second PMS exists (zero benefit), the real app-agnostic unlock is W4b vision, and the change would perturb the live Nadim path for no present gain. **Scope reduced to honest config-registry; PMS-neutral detection/writeback contracts deferred to P2** (designed against a real second example or post-W4b). The config registry, PHI invariant, and Q3 fix are all Codex-clean on their own terms — no R3 needed.
- **Encapsulation finding (post-PhiPolicy, code-grep):** every `PioneerRxConstants` consumer lives **inside the PioneerRx adapter** (`PioneerRxSqlEngine`, `PioneerRxCanarySource`) — status vocab + PHI columns are NOT in Core. The only genuine Core PioneerRx leaks are the `"PioneerPharmacySystem"` catalog literal and the `Pms`/`schemaSignature` labels in `SerializeRxBatch`. **Consequence:** `StatusVocabulary` is **DROPPED** from `AdapterConfig` (PioneerRx status vocab stays adapter-internal; generalizing is N=1 premature). `Pms` label folds into `DisplayName`; `SqlProfile = {DefaultCatalog, SchemaSignature}`. **This supersedes** StatusVocabulary mentions in §4.1/§4.3/§4.6/§7-test4/§8-step3 — drop test 4 + the matcher-migration in step 3. The registry's real value is now: PHI-invariant home + catalog/label leak cleanup + Q3 fix + W3 backbone. (Reinforces that W4b vision-grounding, not config plumbing, is the real app-agnostic unlock.)

## 10. Done criteria

- [ ] Tests 1–7 green; `dotnet test` clean (net8.0 + win-x64 gated).
- [ ] Grep gate (step 8) passes.
- [ ] `ComputerRxAdapterConfig` registers + resolves with zero collision (test 7).
- [ ] `SerializeRxBatch` byte-identical for PioneerRx (test 5).
- [ ] HIPAA invariants #1–#4 each have a guarding test.
- [ ] Codex chunk review (code) before merge.
