# Flywheel Activation — Design Brief

**Status:** Phase-3B SHIPPED (merged to main, #227). Increment 2 (replay-first) BUILT on `feat/replay-first` — pending Codex adversarial + HIPAA review before any box flip. Increments 3–4 DESIGNED, not built.
**Scope:** activate the replay flywheel (harvest verified trajectories → bank → replay deterministically → retrieve as few-shot → distill) without ever letting PHI into `verified_skills`.
**Precedence:** HIPAA (Precedence-1) > replay safety > flywheel throughput. Every ambiguity resolves fail-closed (bank nothing / replay nothing).

The machinery that already exists and is NOT rebuilt here: `VerifiedSkillReplayer` (deterministic cash-in, fail-closed on drift), `VerifiedSkill`/`ActionSignatureParser`/`ReplaySkillFactory`, the `verified_skills` store + decay/retirement in `AgentStateDb`, the post-run harvest call sites in `HeartbeatWorker` (`HarvestVerifiedSkill`, lines ~1800 navigate / ~2010 explore), and the `replay_skill` signed command (~2070). The single blocker was the harvester's click-only hard-stop — nothing from real navigate runs could bank, so the ratchet was inert.

---

## Increment 1 — Phase-3B: scrubbing-aware harvest (THIS BUILD)

### What changed

| File | Change |
|---|---|
| `src/SuavoAgent.Core/Agentic/HarvestPhiCertifier.cs` | NEW — pure, IO-free certification of one step (verb + params + signature) and of the final serialized `steps_json`. NonBacktracking regexes only. Any exception → refuse. |
| `src/SuavoAgent.Core/Agentic/VerifiedTrajectoryHarvester.cs` | Click-only hard-stop (old line ~51) replaced by per-step certification (Gate 3) + end-of-pipe re-certification of the exact persisted string (Gate 4). One uncertifiable step refuses the WHOLE trajectory. Public 3-arg `Harvest` unchanged; new `out string? refusalReason` overload surfaces a PHI-free refusal code. |
| `src/SuavoAgent.Core/Workers/HeartbeatWorker.cs` | `HarvestVerifiedSkill` logs the refusal code (`verified-skill harvest refused run=… reason=…`) — observability for on-box validation. No control-flow change; harvest stays post-run, best-effort, never fails the run. |
| `tests/.../ScrubbedHarvestPhase3BTests.cs` | NEW — 19 tests pinning both properties (see Validation). |

### The certification stack (certify-or-refuse, never transform)

A banked action must replay EXACTLY — `VerifiedSkillReplayer` rejects any step whose reconstructed
signature differs from the banked one — so a scrubbed-but-different value is both useless and dangerous
(it would type `[NAME]` into a live field). The scrubber is therefore a **certifier**: bankable ⇔
scrubbing is a no-op. Anything the catalog would redact refuses the value, and the harvester banks
nothing.

Threat model: the perceived screen is already scrubbed (`AssertScrubbed`), so real patient data can only
enter action params via (a) the free-form objective **Goal** (operator-supplied: "find patient Smith" →
the reasoner types `Smith`) and (b) raw labels echoed by the LLM from the goal instead of the scrubbed
screen. The Helper's `PhiPatternGuard` independently rejects SSN/address/email/phone/NDC-shaped typed
text **before injection** (so such a step never verifies Met) — but it does not catch bare names. Layers:

Certification is **two-pass** (per-step structure, then a cross-step keyboard-stream pass — the latter
added in the Codex HIPAA round-2 hardening to catch PHI split across steps). The harvester calls
`HarvestPhiCertifier.CertifyTrajectory` once over the filtered Met+Success steps.

1. **Executed-and-verified only** — `VerifiedTrajectoryHarvester` banks a step ONLY when
   `Outcome == Success` AND `Verdict == Met`. A Helper-REJECTED step (e.g. PhiPatternGuard rejecting a
   typed email) never banks even if its Verdict reads Met. *(Round-2 #6.)*
2. **Verb allowlist** — only `click_by_label`, `click_by_signature`, `launch_sandbox_app`,
   `type_into_field`, `press_keys`. Anything else (or an unresolvable verb) refuses the trajectory.
3. **Reconstruction equality** — when structured params exist, `NextAction.Act(verb, params).Signature()`
   must equal the banked `ActionSignature` EXACTLY (the same identity the replayer enforces, pulled
   forward to harvest) — so a forged/mismatched `ParamsJson` (a dirty `label` under a clean `text`
   signature) can never persist. Sig-only rows require at least verb-prefix agreement. *(Round-2 #5.)*
4. **Trusted-catalog certification on every banked string** — verb, signature, every param key+value:
   `PhiScrubber.ContainsPhi == false` AND `ScrubText(x) == x` (scrub-idempotence). Both probes fail
   closed internally (timeout → "PHI present" / sentinel).
5. **Structured-params requirement for free-text verbs** — a sig-only `type_into_field`/`press_keys`
   step can't be certified value-by-value (the unescaped signature is not losslessly splittable) →
   refused; the value-bearing key (`text`/`chords`) must be present. Sig-only clicks keep the shipped
   standard (signature-level trusted scan).
6. **Verb-scoped structural keys** — a param key keeps the bounded-structural (trusted-catalog-only)
   standard ONLY for the verbs that legitimately carry it. `label`/`signature`/`automation_id` are
   structural for CLICKS but NOT for `type_into_field`/`press_keys`, so
   `type_into_field(label="Jane Doe")` can no longer smuggle a name through a globally-"structural" key
   — it gets the full free-text vetoes. *(Round-2 #4.)*
7. **Free-text values (typed text — the new surface) get the strictest standard:**
   - trusted-catalog scrub-idempotence;
   - ShadowDenylist **enforced** (NDC / DOB-shape / PioneerRx-id / member-id staged rules; shadow-mode
     elsewhere by design, enforced on this brand-new surface);
   - **email veto** — typed contact PHI the trusted catalog does not cover; *(Round-2 #6.)*
   - **total-digit veto** — strip separators, refuse ≥6 total digits, so space/dot-separated SSN
     (`123 45 6789`) / DOB (`01.02.1990`) / MRN (`123 456`) are caught even though no 6 are contiguous;
     *(Round-2 #2.)*
   - numeric/boolean fast-pass (prices, quantities, flags — only AFTER every shape scan);
   - **name-shape veto** — two consecutive capitalized words ("John Doe", Unicode-letter aware);
   - **goal-echo veto** — any alpha token (≥3 letters, non-stoplist) shared with the Goal refuses,
     compared after **Unicode-normalize + diacritic-fold + case-fold on BOTH sides**, so goal "García"
     catches typed "garcia". Two justifications: (i) the Goal is the PHI provenance channel;
     (ii) goal-echoed text is run-specific — banking it as a literal would replay a stale value. *(Fold
     added round-2 #3.)*
8. **Chord grammar for `press_keys`** — must parse as `(modifier+)*main` with main ∈ named keys /
   single alnum / F1–F12; ≤16 chords; ≤2 single-LETTER mains **per step**.
9. **Cross-step keyboard stream — two scopes.** (a) Per CONTIGUOUS run (segmented by clicks): the typed
   `text` + single-letter chord mains are concatenated and the **full free-text vetoes** run on the
   concatenation (space-joined structure like name-shape / identifier-digits typed into ONE field is
   meaningful here), plus a ≤2 single-letter-mains cap. (b) Over the WHOLE trajectory (clicks do NOT
   reset it): the assembled keystrokes get the letter cap + total-digit veto + a **SUBSTRING** goal-echo
   (a goal token found anywhere inside the keystrokes is a run-specific echo no matter where the agent
   clicked between fragments). Scope (a) catches a name/identifier assembled key-by-key in one field
   (`type "Jo"`+`type "hn"`, or `press J`+`press o`…); scope (b) closes the same literal split ACROSS a
   click into fragments too short for any per-run veto (`type "Jo"`<click>`type "hn"` → "john"). Substring
   (not token-equality) for (b) because cross-field assembly drops the separators the token form relies
   on; it over-refuses a typed value that coincidentally contains a goal token — safe, since a goal-echoed
   literal is run-specific and shouldn't bank, and clean non-echoed text (the screen is scrubbed) has
   none. *(Round-2 #1 + #7 = scope (a); cross-click follow-on (commit `cff27ab`) = scope (b).)*
   NOTE the trajectory letter cap sums only single-letter CHORD mains, never the characters of typed
   `text` — so ordinary multi-field typing ("ab" in a field) is not letter-counted; only `press_keys`
   key-by-key spelling is.
10. **End-of-pipe re-certification** — the exact `SerializeSteps()` string that lands in
    `verified_skills.steps_json` is re-scanned (belt-and-suspenders against shapes assembled across
    value boundaries).
11. **Refusal reasons are operational codes** (`step2:free_text_goal_echo:text`,
    `stream2:keyboard_spelling_veto`, `trajectory:goal_echo`) — never values — so they are safe to log
    and ship.

**Known accepted costs (fail-closed over-refusal, by design):**
- Drug/search terms echoed from the goal ("search Lipitor" → type `Lipitor`) refuse → don't bank.
  Lift later via an on-box drug-lexicon allowlist (Phase-3C candidate) or parameterized skills.
- Two-capitalized-word typed text that isn't a name ("Extra Strength") refuses. Acceptable; banking is
  opportunistic — a refused bank just means the LLM runs again next time.
- Numerics with 6+ digit runs refuse even when legitimate.
- Whole-trajectory **substring** goal-echo (scope (b) above): typed text that coincidentally CONTAINS a
  goal token as a substring across the run refuses (goal "set the dose" + a value containing "dose").
  Broader than the token-equality per-value check by design — the cross-click defense can't rely on
  separators. Tightenable in 3C with the drug-lexicon allowlist; until then, fail-closed.

**Closed in round-2 (all 7 Codex HIPAA findings):** split typed name across steps (#1) + split chord
spelling (#7) → cross-step keyboard-stream pass; space/dot-separated SSN/DOB (#2) → total-digit veto;
Unicode/diacritic surname (#3) → fold-both-sides goal-echo; structural-key smuggling on free-text verbs
(#4) → verb-scoped structural keys; prefix-only verb/sig check (#5) → full reconstruction equality at
harvest; Helper-rejected step banking + missing email rule (#6) → Outcome==Success gate + email veto.

**Known residual, CONSCIOUSLY DEFERRED to Phase-3C (must stay on the Codex/HIPAA review list):**
- A bare-name **click label** ("John Doe" as a row label, no comma, no context keyword) echoed by the
  LLM from the goal still passes the trusted catalog and banks — exactly as it does in the shipped click
  path today. `Doe, John` (LastFirst) IS caught; all-caps `DOE, JOHN` is NOT (catalog rule requires
  mixed case). The name-shape veto is deliberately NOT applied to click labels (it would refuse
  legitimate PMS buttons "Patient Search", "Fill Queue"). **A label goal-echo veto was tried and
  REJECTED:** UI button labels legitimately share vocabulary with the goal ("click Price" to "update the
  price"), so echo is the NORM for labels — the veto broke the canonical pricing flow
  (`update the price to 12.99` → click "Price"). Banking labels is therefore certified by the trusted
  catalog only, same as the shipped click path. Real fix = **bank-time label grounding** (3C): capture
  `label ∈ scrubbed ElementSummary` as a boolean on `StepRecord` at step time in
  `ContextAccumulator.RecordStep` (the screen is in scope) and require it in the certifier for labels —
  the ONLY check that distinguishes a real UI control from an LLM-echoed name without over-refusing. It
  touches the loop contract → its own reviewed change. **Interim exposure** requires: operator puts a
  patient name in the navigate GOAL, the LLM echoes it as a click label (not from the scrubbed screen),
  that click VERIFIES Met, on a box where harvest runs — narrow, but real until 3C.

**CPU/RAM delta:** ~40 short-string regex passes per banked step (trusted 18 + shadow 17 + 5 local),
microseconds each; ≤25 steps per run; runs post-run inside the existing best-effort `Task.Run`. No new
allocations beyond per-step token sets. Zero hot-path impact, zero RAM growth.

**Actuation-gate implications:** none. Harvest is observation-side only; the run already happened
through the normal gates. Banking changes nothing about what may execute.

---

## Increment 2 — Replay-first (fingerprint-match before any Tier-2 LLM call) — BUILT (`feat/replay-first`)

> Build notes (2026-06-11), three logged deltas from the wiring below, all fail-closed strengthenings:
> (1) **Supervised stand-down instead of dry-run replay** — a dry-run dispatch cannot move the screen, so
> a dry-run replay always dies `PostconditionFailed` at step 0 and would FALSELY decay a healthy skill
> (3 supervised navigates would retire it). When the run is dry-run or the composite gate answers anything
> but Allow for the skill's first action, `ReplayFirstRunner` skips the attempt and the loop — which
> dry-runs + escalates exactly as today — keeps the operator boundary. Same gate INSTANCE is consulted,
> so pre-check and replay can never disagree. The "replay without earned autonomy dry-runs + escalates"
> invariant holds — via the loop, without the mis-attribution.
> (2) **steps_hash pin at load** — a banked row that doesn't deserialize or hash back to its stored
> `steps_hash` is treated as Unparseable-class (decay + skip), so a corrupt row can't pin replay-first
> off forever NOR replay tampered steps.
> (3) **`Agent.ReplayFirstAllowTypeSteps` added to the config-override blocklist** (AutoExecution.*
> precedent): the type-step unlock is a deliberate local change after its own review, never a remote flip.
> Entry-StateHash mismatch records NOTHING (an unrelated screen is not the skill's fault); the validation
> drift test's `consecutive_failures` increment is the MID-FLOW StateMismatch (replay started, then
> diverged) — both pinned by tests.

**Goal:** when a navigate/explore objective arrives for a (pharmacy, taskKey, app) that has a healthy
banked skill whose first `StateHash` matches the live screen, run `VerifiedSkillReplayer` INSTEAD of the
agentic loop. LLM only on miss/drift. This is the amortization payoff: solved tasks cost zero reasoning.

**Wiring (exact):**
- `HeartbeatWorker` navigate handler (before constructing the loop at ~1790) and explore handler (~2000):
  1. `_stateDb.GetBestVerifiedSkillForTask(pharmacyId, taskKey, app)` (already exists; excludes retired).
  2. Eligibility: `success_count ≥ 2` (one verification could be luck; two is a tube) AND — **v1 hard
     rule — every step verb is click-family**. Type-bearing skills replay only via the explicit
     operator `replay_skill` command until value-invariance is solved (a banked literal types the OLD
     value; postcondition "screen changed" would pass and certify a semantically wrong write).
  3. One perceive via the same `HelperPerceiver`; compare `ContentHash` to `Steps[0].StateHash`.
     Mismatch → skip replay silently, fall through to the loop (cheap: one IPC perceive).
  4. Match → `VerifiedSkillReplayer.ReplayAsync` with the **same gate the loop would use** — NOT
     `ReplaySkillFactory` as-is (it hardwires `SandboxExploreSafetyGate`, which denies type/press and
     is sandbox-allowlist-only). Add `NavigateReplayFactory` (or a gate parameter on
     `ReplaySkillFactory.Create`) wiring the composite navigate gate from `NavigateLoopFactory`, so
     preflight (kill-switch / pause / never-blind-on-live-PMS) and the M3 autonomy gate apply to
     replayed actions IDENTICALLY to reasoned ones. Replay without earned autonomy dry-runs +
     escalates — correct: replay is still actuation; the autonomy gate is the legal/operator boundary.
  5. `Completed` → ack success, `RecordSkillReplayOutcome(skillId, success: true)`, skip the loop
     (and re-harvest is unnecessary — the skill just re-verified itself).
     Any non-Completed → `RecordSkillReplayOutcome(…, false)` for skill-fault outcomes only
     (StateMismatch / PostconditionFailed / Unparseable / StepRejected — same set as the `replay_skill`
     handler), then **fall through to the agentic loop**, which perceives fresh — a partial replay
     leaves the app mid-flow and the loop's whole design is "reason from the current screen".
- Flag: `Agent:ReplayFirst` (new bool on `AgentOptions`, default **false**), flipped per-box after the
  sandbox validation below. Later `Agent:ReplayFirstAllowTypeSteps` (default false) once parameterized
  skills exist. (`LearningMode` at `AgentOptions.cs:101` is the unrelated 30-day-observation flag — do
  not overload it.)

**CPU/RAM:** saves an entire LLM/cloud reasoning chain on hit (the point); costs one perceive + one
SQLite SELECT on miss. No RAM delta.

**HIPAA:** replay executes only Phase-3B-certified strings; perception during replay passes the same
`AssertScrubbed`. No new data leaves the box.

**Validation:** unit — fingerprint hit/miss/fallthrough matrix with `FakeSafetyGate` + scripted sim;
integration — sandbox loop on the box (below); plus a drift test: bank, mutate the sim's first state,
assert loop fallback + `consecutive_failures` increments.

## Increment 3 — Retrieval-as-few-shot (when the LLM IS called) — DESIGN ONLY

**Goal:** on Tier-2 escalation, inject 1–3 nearest banked trajectories into the proposal prompt as
worked examples — the LLM imitates its own verified history instead of re-deriving.

**Wiring (exact):**
- New `AgentStateDb.GetVerifiedSkillsForApp(pharmacyId, app, limit)` (simple SELECT, newest/most-confirmed
  first).
- Ranking: **lexical/structural only, no embedder** — score = token overlap between the live objective
  Goal + current `WindowTitle` and the skill's `task_key` + step labels parsed from banked signatures
  (`ActionSignatureParser.TryParse` → label params), tie-break on `success_count`. ~tens of skills per
  app — trivial.
- Injection: `NavigateReasoning.BuildContext` already threads `Flags["prior_actions"]`; add
  `Flags["known_skills"]` = up to 3 compact transcripts (`task.calc: click 7 → click x → click 8`),
  hard-capped (~600 chars) so the prompt stays bounded. Tier-1 rule brain untouched.
- Flag: `Agent:SkillRetrievalFewShot`, default false.

**CPU/RAM:** one SELECT + string scoring per Tier-2 call (already a multi-second LLM path) — noise.

**HIPAA:** banked content is Phase-3B-certified PHI-free, so prompt injection introduces no new
exposure; the outbound guard still scrubs the assembled prompt as today. The few-shot block is
advisory text — the reasoner's output still goes through grounding + the composite gate + postconditions,
so a hallucinated imitation cannot bypass anything.

**Validation:** unit — ranking determinism, char caps, no-skills → no flag; A/B on-box — same objective
with/without flag, compare steps-to-Done and cloud-call count.

## Increment 4 — Flag flips + rollout order

1. Phase-3B merges → harvest active everywhere immediately (it only WIDENS what can bank; refusal codes
   land in logs). No flag needed — banking is passive and certified.
2. Box validation (below) → flip `Agent:ReplayFirst` on the demo box (8DC472B9) → canary on first pilot.
3. `Agent:SkillRetrievalFewShot` after replay-first soaks (it's lower risk but builds on the same store).
4. Parameterized skills / drug-lexicon / label-grounding = Phase-3C+, each its own reviewed build.

---

## Validation plan — PMS-less live box (8DC472B9, no PioneerRx)

Proves the ratchet end-to-end with $0 blast radius. The sandbox gate is click-only, and Phase-3B keeps
click-only sandbox harvests banking exactly as before — so the existing proven explore path exercises
the NEW code end-to-end.

1. **Baseline:** confirm agent ≥ the release carrying this branch; active unlocked session; actuation
   gate armed (re-arm via reboot if a prior OTA disarmed it).
2. **Harvest:** dispatch `explore_sandbox` (or `navigate_app` scoped to an allowlisted app) — objective
   e.g. Calculator `compute 7+5` (the UIA-verified `calc_verified` flow). Expect run → Done, then log
   line `verified-skill banked run=… skill=… steps=N successCount=1`. Verify the row:
   `SELECT task_key, app, success_count, length(steps_json) FROM verified_skills` via the state-DB
   inspection path — and grep `steps_json` for `label=`-only params (no PHI fields by construction).
3. **Refusal observability (new):** dispatch a navigate objective that intentionally types goal-echoed
   text in an allowlisted app (e.g. Notepad, goal "type hello world in notepad"). Expect NO bank +
   `verified-skill harvest refused run=… reason=step…:free_text_goal_echo:text` — the code, not the value.
4. **Replay (cash-in):** put the app at the banked entry state; dispatch `replay_skill`
   (`skillId` explicit, or `taskKey`+`app` for most-confirmed). Expect ack
   `outcome=Completed, steps_completed == banked N`, `success_count` incremented (thicken), zero
   LLM/cloud-reason calls in the run logs.
5. **Drift fail-closed:** dispatch `replay_skill` with the app on the WRONG screen. Expect
   `StateMismatch` at step 0, `steps_completed=0`, `consecutive_failures` +1, no actions taken.
6. **Re-verify determinism:** repeat step 2 with the same objective — expect the SAME `skill_id`
   (`success_count` 1→2), not a duplicate row.

Pass = the full loop: derive once (LLM) → bank (certified) → replay forever (no LLM) → thicken/decay.

---

## Risk + review — what Codex adversarial + HIPAA review MUST scrutinize

1. **Certification recall is regex-bound.** The trusted catalog is context/shape-anchored; the
   name-shape + goal-echo vetoes close the bare-name gap for TYPED text, but certification is still a
   denylist+heuristic stack, not a proof in the cryptographic sense. Attack it: PHI forms that dodge all
   layers — uncapitalized full names NOT present in the goal ("john doe" typed with no goal provenance:
   what realistic path produces one?), names split across two steps' params, unicode homoglyph/zero-width
   evasion (LLM output could contain `Jоhn` with Cyrillic о — regexes are ASCII-anchored; consider an
   ASCII-only veto for free text as a hardening), PHI in param KEYS (scanned, but only trusted-tier).
2. **The bare-name click-label residual (pre-existing).** Confirm the Phase-3C label-grounding plan is
   the right closure and the interim exposure is acceptable: it requires the operator to put a patient
   name in the GOAL of a navigate run whose click on that name VERIFIES Met, on a box where harvest
   runs. Quantify, don't hand-wave.
3. **Goal-echo tokenization edges + dual semantics.** Two goal-echo checks now coexist: per-value
   TOKEN-equality (`SharesGoalToken`, on individual typed/structural values, fold-both-sides) and a
   whole-trajectory SUBSTRING check (`ContainsGoalSubstring`, on the cross-click assembled keystrokes).
   Review both: stoplist entries can mask real echoes (a patient literally surnamed "Save" is
   unstoplisted — check every entry); tokens <3 chars (initials "Jo") are exempt from BOTH (the
   cross-click substring closes the split-fragment case but a lone 2-char value with no later fragment
   still slips); per-value token-equality means "Smithson" typed vs goal "smith" does NOT match at the
   value level, but the trajectory substring WOULD match it if "smith" is a goal token — confirm the
   asymmetry is understood and acceptable. The substring check is intentionally broad (over-refuses
   coincidental contains) — verify that tradeoff for the pharmacy vocabulary.
4. **TOCTOU between replay perceive and act** (replayer step-N perceive → act): unchanged from the
   shipped replayer (Codex Q1 accepted: next step's state-match catches a wrong landing) — but
   re-examine it for type/press replay specifically: a focus steal between state-confirm and
   `type_into_field` types into the WRONG window. The Helper-side focused-target binding
   (`_activeTarget`) is the mitigation — verify it's enforced for type/press on the live path before
   `Agent:ReplayFirstAllowTypeSteps` ever flips. This is the #1 reason type-bearing replay stays
   operator-explicit in v1.
5. **Value-invariance / staleness of banked literals.** Even certified-clean typed values can be
   semantically stale at replay (old price). Postconditions only verify "screen changed". Until
   parameterized skills exist, confirm the v1 rule (replay-first auto-fires click-only skills ONLY)
   is enforced in code, not convention.
6. **Whole-trajectory refusal as a DoS on learning.** One uncertifiable step refuses everything —
   correct for PHI, but verify an adversarial/degenerate objective can't permanently starve banking for
   a task that has a clean alternative path (it can't: refusal is per-trajectory, and a later clean run
   banks; the conductance store still learns edges — confirm).
7. **Serialized-steps end-of-pipe scan** uses the trusted catalog on a JSON string — JSON escaping
   (`A`) could in principle hide a shape from the regex while the value-level scans saw the raw
   string (they did — values are scanned pre-escape; the pipe scan is redundancy, not the primary).
   Confirm the layering argument.
8. **Refusal-reason hygiene.** Codes carry param keys + rule names, never values — verify no code path
   interpolates a value into a reason (tests pin several; review the rest).
9. **Concurrency debt (known, documented at `AgentStateDb.cs:31`):** per-feature locks don't serialize
   against unlocked pricing-path methods on the same SQLite connection. Unchanged by this build
   (harvest remains the only writer, best-effort) — but replay-first adds a READ on the navigate hot
   path; confirm the `_verifiedSkillLock` covers `GetBestVerifiedSkillForTask` (it does today).
10. **Gate parity for replay-first (increment 2):** the single highest-severity item — replayed actions
    MUST flow through the composite navigate gate (preflight + M3 autonomy + dry-run), not the sandbox
    gate shortcut. Reject any implementation that reuses `ReplaySkillFactory` unmodified for navigate
    replay.
