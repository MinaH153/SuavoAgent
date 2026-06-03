# Track 3 PHI Compile-Time CI Gate — Design

> The compile-time enforcement layer that turns the Track 3 PHI invariant from runtime / discipline-enforced into structural code-level enforced. Wave 1 Sub-project A of the v4 meta-roadmap.

**Locked date:** 2026-05-01
**Status:** v0.1 draft (locks to v1.0 after Wave 1 gate trips)
**Owner:** Joshua Henein
**Wave:** 1, Sub-project A
**Source spec:** `docs/superpowers/specs/2026-05-01-suavoagent-v4-roadmap-design.md` §4 Wave 1 + §9 invariant 1
**Depends on:**
- `docs/self-healing/field-registry.md` (5-tier classification — canonical PHI taxonomy)
- `docs/self-healing/event-registry.md` (consumed by analyzer for Outbound type list)
- `docs/self-healing/audit-schema.md` (audit chain)

---

## 0 · Why this spec exists

The v4 meta-roadmap's §9 invariant 1 (cross-cutting, in-force from Wave 1 onward):

> **Track 3 — PHI compile-enforcement.** Any `PHI-Direct` field from `field-registry.md` appearing in any outbound schema (events, verbs, sync payloads, heartbeat, model prompts) = build-fails. CI-enforced, not discipline-enforced.

Today, PHI exclusion in SuavoAgent is enforced at runtime by `PhiScrubber` (multi-pass regex + pattern matching) and structurally by hand-curated payload types like `PatientDetailsPayload`. Both mechanisms work but neither is build-time-enforced — a developer can introduce a new outbound type with a PHI field and the CI tests will pass right up until the runtime scrubber catches it (or doesn't).

This spec turns that situation into a build-time error. A PR that adds PHI to an outbound schema fails CI (and IDE squigglies fire at edit time). The HIPAA invariant becomes structural.

---

## 1 · Architecture

Two parallel enforcement systems, one per language, sharing a unified mental model:

```
SuavoAgent (.NET 8)              Suavo (TypeScript / Next.js)
═══════════════════              ════════════════════════════

[PhiDirect] attribute            phi(z.string()) wrapper
       ↓                                 ↓
applied to leaf fields           applied to leaf schemas
       ↓                                 ↓
that appear inside                that appear inside
       ↓                                 ↓
[OutboundPayload] type           outbound(z.object({...}))
       ↓                                 ↓
Roslyn diagnostic analyzer       Custom ESLint rule
runs on every dotnet build       runs on every lint pass
       ↓                                 ↓
emits SUAVO0001 error            emits suavo-phi/no-phi-in-outbound
       ↓                                 ↓
       └────────── IDE squigglies + CI red ──────────┘
                                ↓
                  Synthetic PHI-leak canary fixture
                  (test-suite resident)
                  proves the gate works
```

### Source of truth

- **"Is this field PHI-Direct?"** → the `[PhiDirect]` / `phi()` marker on the field declaration. Authoritative for enforcement.
- **"Is this type outbound?"** → the `[OutboundPayload]` / `outbound()` marker on the type declaration. Authoritative for enforcement.
- **`field-registry.md`** remains the canonical human-readable doc. CI cross-check between registry ↔ decorators is **deferred** to a future wave (drift detection adds complexity; not blocking the gate's safety).

### Long-term trajectory (informational)

- **Wave 1 ships Approach A** (type-marker only — what this spec implements).
- **Wave 5 adds Approach B** (sink markers like `[OutboundCall]` on `SuavoCloudClient.PostAsync<T>`) when Track 5's signed-verb dispatch ships its first real network sink.
- **End state by Wave 5:** belt-and-suspenders D-style enforcement (type marker + sink marker + canary) — multiplicative safety. Both halves land where their second-order code already lives; no premature refactor.

### Wave 1 coverage scope

The gate ("synthetic canary fails CI build") does NOT require comprehensive retrofit of every existing payload. Wave 1 ships:

- The analyzer + ESLint rule themselves (the enforcement machinery)
- A handful of representative types retrofitted as proof (3 each side, listed in §2)
- The canary fixture
- Wired into `SuavoAgent.Contracts.csproj` + Suavo's ESLint config

Future PRs progressively retrofit remaining outbound types. The gate is meaningful from Wave 1 onward; coverage compounds across waves.

---

## 2 · Components

### SuavoAgent .NET (4 components)

| Component | Path | What it does |
|---|---|---|
| `[PhiDirect]` attribute | `src/SuavoAgent.Contracts/Annotations/PhiDirectAttribute.cs` | Marks a property/field as PHI-Direct per `field-registry.md` tier classification. `[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false, Inherited = true)]`, no params. |
| `[OutboundPayload]` attribute | `src/SuavoAgent.Contracts/Annotations/OutboundPayloadAttribute.cs` | Marks a record/class/struct as a network-bound payload type. `[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface, AllowMultiple = false, Inherited = true)]`, no params. |
| Roslyn analyzer project | `src/SuavoAgent.Analyzers/SuavoAgent.Analyzers.csproj` (new csproj, targets `netstandard2.0` per Roslyn requirement) | Hosts `PhiInOutboundPayloadAnalyzer : DiagnosticAnalyzer` with rule ID `SUAVO0001`. Walks every compiled named type; if `[OutboundPayload]` type contains any `[PhiDirect]` property/field (recursively through nested type references with cycle guard), emits compile error with field path. Also defines `SUAVO0099` for self-crash recovery. |
| Analyzer tests | `tests/SuavoAgent.Analyzers.Tests/SuavoAgent.Analyzers.Tests.csproj` (new csproj, references `Microsoft.CodeAnalysis.CSharp.Analyzer.Testing.XUnit`) | Synthetic canary fixtures: code snippets with intentional violations assert `SUAVO0001` fires; clean snippets assert silent. Plus the perf/stress test (§5). |

**Wired in** via `<ProjectReference Include="...SuavoAgent.Analyzers.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />` in every consuming project's `.csproj`. Wave 1 wires `SuavoAgent.Contracts.csproj`. Future waves wire other projects as their types are retrofit.

### Suavo TypeScript (3 components)

| Component | Path | What it does |
|---|---|---|
| Zod marker helpers | `src/lib/zod-phi.ts` | `export const phi = <T extends z.ZodType>(schema: T): T => schema` — identity at runtime, syntactic marker for ESLint. Same for `outbound`. Usage: `phi(z.string())`, `outbound(z.object({...}))`. |
| ESLint rule | `eslint-rules/no-phi-in-outbound.js` (registered as a local plugin via Suavo's existing ESLint config — flat config or legacy detected at implementation time) | Rule name `suavo-phi/no-phi-in-outbound`. Walks AST: for each `outbound(arg)` call, recursively scans arg subtree for any `phi(...)` call — if found, emits error with code-frame location. |
| Rule tests | `eslint-rules/__tests__/no-phi-in-outbound.test.ts` (vitest + ESLint `RuleTester`) | Synthetic canary fixtures: violating + non-violating snippets, asserts rule fires correctly on each. Plus the perf/stress test (§5). |

### Wave 1 retrofit scope (the "handful of representative types")

**SuavoAgent — mark 3 types `[OutboundPayload]`:**
1. `PatientDetailsPayload` — already PHI-minimum by design; formalize the intent. None of its fields need `[PhiDirect]` (per field-registry, address fields are PHI-Adjacent salted, not PHI-Direct).
2. `WaveGateTrippedPayload` — `[OutboundPayload]`, no PHI fields.
3. `WaveGateFailedPayload` — `[OutboundPayload]`, no PHI fields.

**Suavo — mark 2 Zod schemas with `outbound()`:**
1. `WaveGateTrippedPayloadSchema` (already shipped Wave 0)
2. `WaveGateFailedPayloadSchema` (already shipped Wave 0)

**Note on `RxOrderCandidate`:** Wave 4 (Extraction E2E) will refactor `RxOrderCandidate` to split outbound-safe shape vs local-only PHI shape — exactly the kind of refactor this gate forces. Skip retrofitting it in Wave 1; address in Wave 4 when its full shape is defined.

### Synthetic canary

Lives as test fixtures inside each language's analyzer test suite. Not a separate "intentionally failing PR." Reasons: deterministic, runs every test pass, discoverable, no PR-rot.

The canary asserts:
- Violating fixture (C#): `[OutboundPayload] class CanaryLeak { [PhiDirect] string PatientName }` → analyzer emits `SUAVO0001`
- Non-violating fixture (C#): `[OutboundPayload] class CleanPayload { string RxHash }` → analyzer silent
- Equivalent in TS: `outbound(z.object({ patient_name: phi(z.string()) }))` → ESLint errors; `outbound(z.object({ rx_hash: z.string() }))` → silent

(An "audit-demo PR" left open-but-never-merged for HITRUST/HIPAA storytelling is a Phase I nice-to-have, deferred.)

---

## 3 · Data flow

### SuavoAgent C# analyzer (symbol-based, type-graph traversal)

```
Roslyn compilation pipeline
        ↓
RegisterSymbolAction(AnalyzeNamedType, SymbolKind.NamedType)
        ↓
For each named type symbol:
  1. Check: does symbol carry [OutboundPayload]?
     → if no, return (skip)
  2. Initialize:
       visited = HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default)
       path    = stack<string>     // for diagnostic messages
  3. WalkMembersRecursive(symbol, visited, path, depth=0):
       if depth > 50: emit SUAVO0099 ("excessive nesting"); return
       if type ∈ visited: return        // cycle guard
       visited.Add(type)
       foreach member in type.GetMembers():
         if member is IPropertySymbol or IFieldSymbol:
           if member has [PhiDirect]:
             emit SUAVO0001 with:
               Location: member.Locations.First()
               Message:  "PHI-Direct field {Outbound}.{path}.{member.Name}
                          on outbound payload — split required (move to
                          local-only type or remove the [PhiDirect] marker)."
           if member.Type is INamedTypeSymbol nestedType
              AND nestedType.ContainingAssembly == compilation.Assembly:
             // skip framework types and assembly externals — only walk our own
             path.Push(member.Name)
             WalkMembersRecursive(nestedType, visited, path, depth + 1)
             path.Pop()
```

- **Why symbol-based, not syntax-based:** C# allows nested types across files; the analyzer must follow the type graph. Roslyn's symbol model resolves cross-file references for free.
- **Cycle guard:** `visited` set with `SymbolEqualityComparer.Default` prevents infinite recursion on self-referencing types.
- **Depth cap:** 50 levels. Anything deeper is a code smell; emit `SUAVO0099` and stop.
- **Assembly filter:** only walk types defined in the same assembly; framework types and external libraries are out of scope (we don't control their shape).
- **Diagnostic precision:** `path` stack produces messages like `"PHI-Direct field RxOrderCandidate.PatientFields.Address1 on outbound payload"` — pinpoints the leak location through nested types.
- **Crash recovery:** the entire `AnalyzeNamedType` body is wrapped in `try/catch`. On unhandled exception, emit `SUAVO0099 "Analyzer crashed analyzing type {Name}: {ex.GetType().Name}"`. Build fails LOUDLY rather than silently (Roslyn's default is silent suppression — unacceptable for a HIPAA invariant).

### Suavo TypeScript ESLint rule (syntactic AST traversal)

```
ESLint visitor hooks: CallExpression
        ↓
For each CallExpression where callee.name === 'outbound':
  1. Iterate over arguments
  2. ScanForPhi(arg, ruleContext, depth=0):
       if depth > 50: ruleContext.report({ messageId: 'tooDeep' }); return
       if arg is CallExpression && arg.callee.name === 'phi':
         ruleContext.report({
           node: arg,
           messageId: 'phiInOutbound',
           data: { description: 'phi(...) inside outbound(...)' }
         })
         // continue scanning — multiple violations possible
       recursively descend into arg's children
       (ObjectExpression properties, ArrayExpression elements,
        nested CallExpressions, ConditionalExpression branches, etc.)
```

- **Why syntactic, not type-aware:** ESLint AST traversal is fast (no `tsc` overhead). Trade-off: doesn't follow identifier references across variables/files.
- **Wave 1 constraint:** `phi(...)` must appear syntactically nested inside `outbound(...)`. Factoring out a sub-schema with `phi()` and referencing as an identifier won't be caught (rule documents this clearly with a fix-suggestion: "inline phi() within outbound() or use a dedicated outbound-only sub-schema").
- **Future enhancement (Wave 5+):** type-aware variant using `@typescript-eslint/parser`'s scope manager to follow identifier references — when Track 5 verbs add more outbound surface area.
- **Crash recovery:** wrap visitor body in `try/catch`; on catch, emit `internalRuleError` messageId so CI fails. ESLint's default also silently passes after rule crashes — same unacceptable default.

### Failure rendering (both languages)

| Surface | C# | TS |
|---|---|---|
| **IDE** | Red squiggle on violating member; tooltip shows `SUAVO0001` + path message | Red underline on `phi(...)` call inside `outbound(...)`; tooltip shows rule name + suggestion |
| **`dotnet build` / `pnpm lint`** | Compile error with file:line | ESLint error with file:line + code frame |
| **CI** | `dotnet test` / build step fails; build step reports diagnostic | `eslint` step fails on PR; GitHub annotates the line |

### Synthetic canary execution flow

```
test runtime (xUnit for C#, vitest for TS):

1. Test fixture defines violating code snippet as string
2. C# uses CSharpAnalyzerTest<TAnalyzer, TVerifier>;
   TS uses ESLint RuleTester
3. Test pipeline compiles/lints the snippet
4. Assert: expected diagnostic is emitted
   (positive case: SUAVO0001 / no-phi-in-outbound fires)
5. Mirror test with clean snippet
6. Assert: no diagnostic emitted (negative case)
```

The synthetic canary IS a passing unit test — its passing means "the gate works." If the analyzer is broken or removed, the canary test fails immediately.

---

## 4 · Error handling

The analyzer is code that runs at build time; "errors" here mean failure modes of the gate itself.

| Failure mode | Mitigation |
|---|---|
| **Analyzer throws unhandled exception during type analysis** | Wrap analysis body in `try/catch`. On catch, emit `SUAVO0099` ("Analyzer crashed analyzing type X — see inner exception"). Build fails LOUDLY rather than silently. Roslyn's default behavior is silent suppression, which would create a false sense of security for a HIPAA-existential invariant. |
| **ESLint rule throws during AST walk** | Same pattern — wrap visitor logic in `try/catch`; on catch, emit `internalRuleError` messageId so CI fails. ESLint's default also silently passes after rule crashes. |
| **Performance — analyzer too slow on large compilations** | Hard-cap recursion depth at 50 levels. Use `SymbolEqualityComparer.Default` for visited set. Run perf test on every CI build (§5); fail if any project's analyzer pass exceeds threshold. |
| **False positives — type carrying PHI legitimately for local persistence flagged as Outbound** | Not a false positive — that's a wrong marker. The fix is to NOT mark the type `[OutboundPayload]` (it's a local type) OR split it into outbound vs local-only types. Existing `PatientDetailsPayload` ↔ `RxPatientDetails` split is the canonical pattern. |
| **False negatives — network-bound types never marked `[OutboundPayload]` (silent leak)** | The known Approach-A weakness. Wave 1 mitigation: code review + this spec's documentation. Wave 5 adds Approach B (sink markers) → D-in-practice catches this from the sink side. |
| **field-registry.md drifts from `[PhiDirect]` decorators** | Wave 1 explicitly defers drift detection. Spec documents the deferral. Decorators are the enforcement source of truth; registry is canonical docs. Audit trail captures every PR adding/removing a decorator. |
| **ESLint flat config vs legacy `.eslintrc`** | Implementation plan task detects which Suavo uses and wires the rule appropriately. Pin in the task. |
| **Analyzer breaks across .NET SDK upgrades** | Pin `Microsoft.CodeAnalysis.CSharp.Workspaces` version; target `netstandard2.0` (broadest compat). Bump deliberately when Suavo upgrades .NET. |
| **Cross-file type references slow analyzer down** | Cache the visited set per compilation, not per type. Roslyn caches symbol resolution automatically; we don't re-resolve. |

**Crash-loud is the operating principle** — silent failure of a HIPAA-existential invariant is unacceptable. Better to fail builds with `SUAVO0099` than to ship code that thinks it's protected.

---

## 5 · Testing strategy

Five test categories, all must-have for Wave 1:

### 1. Analyzer / rule unit tests *(must-have)*

**C#:** uses `Microsoft.CodeAnalysis.CSharp.Analyzer.Testing.XUnit`. Loads code snippets, runs analyzer, asserts diagnostics emitted (or not).

**TS:** uses ESLint's built-in `RuleTester` (or `@typescript-eslint/rule-tester` if Suavo already has the typescript-eslint stack).

**Cases each side:**
- Direct violation: `[OutboundPayload]` type with `[PhiDirect]` field → emits `SUAVO0001`
- Nested violation: `[OutboundPayload]` Foo containing `Bar` (no marker) containing `[PhiDirect]` → emits with path
- Two-level nested violation: A → B → C with PHI on C → emits
- Cyclic types: A references B, B references A → analyzer terminates (cycle guard works)
- Clean outbound: `[OutboundPayload]` with only Operational/Public fields → silent
- Non-outbound type with PHI (local-only): no `[OutboundPayload]` marker → silent (correctly)
- Multiple violations on one type: reports each independently
- Crash recovery: synthetic exception inside analyzer → `SUAVO0099` emitted

### 2. The canary fixture *(must-have)*

Lives in test suite. Asserts gate works on representative violation. Already detailed in §3.

### 3. Regression tests for retrofitted types *(must-have)*

For each Wave 1 retrofit (`PatientDetailsPayload`, `WaveGateTrippedPayload`, `WaveGateFailedPayload`, plus their Zod equivalents):
- Assert: analyzer silent on this type (currently clean)

Acts as a watchdog — if a future PR adds PHI to one of these types, the test fires.

### 4. Build integration test *(must-have)*

**C#:** fixture project at `tests/SuavoAgent.Analyzers.IntegrationTest/` with intentional violation. CI step: `dotnet build` on this project must fail with `SUAVO0001`. Asserts the analyzer is wired correctly via `<ProjectReference OutputItemType="Analyzer">`.

**TS:** fixture file in test dir with intentional violation. CI step: `pnpm lint` on the fixture must fail with `suavo-phi/no-phi-in-outbound`. Asserts the rule is wired into Suavo's ESLint config.

### 5. Performance / stress test *(must-have)*

**Why must-have:** HIPAA-existential invariant must scale. Wave 5+ will add many new outbound types via Track 5 verbs; performance regression must be caught early. CI baseline locks in.

**C# implementation:**
- Programmatically generate 1000 records with `[OutboundPayload]` + nested type refs (5+ deep, including a few cyclic loops and a few clean-only chains)
- Run analyzer on the synthetic compilation; measure wall-clock via `Stopwatch`
- Assert under threshold: 5 seconds total (CI machine baseline)
- Threshold doubles allowed under macOS arm64 (slower CI), single across Linux x64

**TS implementation:**
- Programmatically generate 1000 large `outbound()` Zod schemas in a fixture file
- Run ESLint on the fixture; measure via `--cache=false --timing`
- Assert under threshold: 2 seconds (ESLint AST walk is faster than Roslyn symbol resolution)

**Run cadence:** every CI build (not just nightly). Perf regressions caught at PR review time, not after merge. If a PR causes a 20%+ regression, build fails with diagnostic message naming the regressing PR.

**Coverage target:** every analyzer-test category 100% covered (small surface area, achievable). Wave 1 ships ~25 unit tests + 4 integration tests + 8 regression tests + 2 perf tests across both languages.

---

## 6 · Out-of-scope (deferred to future waves)

- **Approach B (sink markers `[OutboundCall]`):** Wave 5 deliverable. Adds sink-side enforcement to complement type-side.
- **Type-aware ESLint rule (cross-file identifier resolution):** Wave 5+. Requires `@typescript-eslint/parser` + `parserOptions.project` overhead.
- **Drift detection between `field-registry.md` and decorators:** Future wave (TBD). Two-way CI cross-check between markdown and code.
- **Audit-demo PR** (one left open-but-never-merged for HITRUST/HIPAA storytelling): Phase I nice-to-have.
- **Roslyn analyzer NuGet packaging** (if SuavoAgent ever splits into separate solutions or open-sources): Future.
- **Auto-fix code-action provider** (suggest fix-it that splits a type into outbound + local halves): Future, requires significant Roslyn code-action engineering.

---

## 7 · Cross-references

### Existing canon (read these before deep work)

- `docs/self-healing/field-registry.md` — 5-tier classification (Public / Operational / PHI-Adjacent / PHI-Direct / Secret) with full inventory
- `docs/self-healing/event-registry.md` — all event types canonical (40+ across 11 domains, including `wave.*`)
- `docs/self-healing/audit-schema.md` — hash-chained audit chain
- `src/SuavoAgent.Contracts/Models/PatientDetailsPayload.cs` — canonical example of the intended pattern (informal today; this spec formalizes it)
- `src/SuavoAgent.Core/Learning/PhiScrubber.cs` — runtime PHI redaction (complementary to compile-time gate; remains in place)

### Source spec

- `docs/superpowers/specs/2026-05-01-suavoagent-v4-roadmap-design.md` — meta-roadmap (this spec is its Wave 1 Sub-project A)

### External references (Roslyn + ESLint patterns)

- Roslyn analyzer tutorial: https://learn.microsoft.com/dotnet/csharp/roslyn-sdk/source-generators-overview (paired with `Microsoft.CodeAnalysis.CSharp.Analyzer.Testing.XUnit`)
- ESLint custom rule tutorial: https://eslint.org/docs/latest/extend/custom-rules
- Existing community precedent: `eslint-plugin-security`, `eslint-plugin-no-secrets` — same shape (semantic AST scan, custom messageIds)

---

## 8 · Change log

- **2026-05-01 v0.1** — Initial draft from brainstorming session. Locks to v1.0 after Wave 1 gate trips (synthetic canary fails CI build + dashboard correctly shows unhealthy state on Joshua's test box per meta-roadmap §4 Wave 1 gate).
