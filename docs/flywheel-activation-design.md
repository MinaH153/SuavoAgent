# Flywheel Activation — Design Brief

**Status:** Phase-3B BUILT on this branch (`feat/phase3b-scrubbing-aware-harvest`); increments 2–4 DESIGNED, not built.
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

1. **Verb allowlist** — only `click_by_label`, `click_by_signature`, `launch_sandbox_app`,
   `type_into_field`, `press_keys`. Anything else (or an unresolvable verb) refuses the trajectory.
2. **Trusted-catalog certification on every banked string** — verb, signature, every param key+value:
   `PhiScrubber.ContainsPhi == false` AND `ScrubText(x) == x` (scrub-idempotence). Both probes fail
   closed internally (timeout → "PHI present" / sentinel).
3. **Structured-params requirement for free-text verbs** — a sig-only `type_into_field`/`press_keys`
   step can't be certified value-by-value (the unescaped signature is not losslessly splittable) →
   refused. Sig-only clicks keep the shipped standard (signature-level trusted scan).
4. **Free-text values (typed text — the new surface) get the strictest standard:**
   - ShadowDenylist **enforced** (NDC / DOB-shape / PioneerRx-id / member-id staged rules; shadow-mode
     elsewhere by design, enforced on this brand-new surface);
   - length cap (256);
   - ≥6-digit-run veto (Rx# / MRN / member-id shaped numerics carry no catalog context keyword);
   - numeric/boolean fast-pass (prices, quantities, flags — already past the catalog+shadow scans);
   - **name-shape veto** — two consecutive capitalized words ("John Doe" has no catalog context
     keyword; this is the layer that catches it);
   - **goal-echo veto** — any alpha token (≥3 chars, non-stoplist, case-insensitive) shared with the
     Goal refuses. Two independent justifications: (i) the Goal is the PHI provenance channel;
     (ii) goal-echoed text is run-specific by construction — banking it as a literal would replay a
     stale value. Those strings are the *holes* of a future parameterized template, not constants.
5. **Chord grammar for `press_keys`** — must parse as `(modifier+)*main` with main ∈ named keys /
   single alnum / F1–F12; ≤16 chords; ≤2 single-LETTER mains (PHI cannot be spelled key-by-key).
6. **End-of-pipe re-certification** — the exact `SerializeSteps()` string that lands in
   `verified_skills.steps_json` is re-scanned (belt-and-suspenders against shapes assembled across
   value boundaries).
7. **Refusal reasons are operational codes** (`step2:free_text_goal_echo:text`) — never values — so
   they are safe to log and ship.

**Known accepted costs (fail-closed over-refusal, by design):**
- Drug/search terms echoed from the goal ("search Lipitor" → type `Lipitor`) refuse → don't bank.
  Lift later via an on-box drug-lexicon allowlist (Phase-3C candidate) or parameterized skills.
- Two-capitalized-word typed text that isn't a name ("Extra Strength") refuses. Acceptable; banking is
  opportunistic — a refused bank just means the LLM runs again next time.
- Numerics with 6+ digit runs refuse even when legitimate.

**Known residual (pre-existing, NOT introduced here, must be on the Codex/HIPAA review list):**
- A bare-name **click label** ("John Doe" as a row label, no comma, no context keyword) echoed by the
  LLM from the goal passes the trusted catalog and banks — exactly as it does in the shipped click
  path today. `Doe, John` (LastFirst) IS caught; all-caps `DOE, JOHN` is NOT (catalog rule requires
  mixed case). Real fix = **bank-time label grounding** (Phase-3C): capture
  `label ∈ scrubbed ElementSummary` as a boolean on `StepRecord` at step time in
  `ContextAccumulator.RecordStep` (the screen is in scope), and have the certifier require it for
  labels. Deliberately out of scope here — it touches the loop contract, deserves its own reviewed
  change. The name-shape veto was NOT applied to labels because it would refuse most legitimate PMS
  buttons ("Patient Search", "Fill Queue").

**CPU/RAM delta:** ~40 short-string regex passes per banked step (trusted 18 + shadow 17 + 5 local),
microseconds each; ≤25 steps per run; runs post-run inside the existing best-effort `Task.Run`. No new
allocations beyond per-step token sets. Zero hot-path impact, zero RAM growth.

**Actuation-gate implications:** none. Harvest is observation-side only; the run already happened
through the normal gates. Banking changes nothing about what may execute.

---

## Increment 2 — Replay-first (fingerprint-match before any Tier-2 LLM call) — DESIGN ONLY

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
3. **Goal-echo tokenization edges.** Stoplist entries can mask real echoes (a patient literally surnamed
   "Save" is unstoplisted — but check every entry); tokens <3 chars (initials "Jo") are exempt; the veto
   compares tokens, not substrings ("Smithson" typed vs "smith" in goal does NOT match — is that
   acceptable?).
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
