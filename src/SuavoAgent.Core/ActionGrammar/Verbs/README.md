# `SuavoAgent.Core.ActionGrammar.Verbs`

Scaffolding for action-grammar v1 verb implementations. **Empty on purpose.**

> No verbs are registered, dispatched, or executed until the Nadim pilot
> (Saturday 2026-04-25) stabilises and Phase A items A1/A2 close. See
> `docs/self-healing/action-grammar-v1.md`.

## Structure

Each verb is its own file under this folder:

```
Verbs/
├── RestartService/
│   ├── RestartServiceVerb.cs            # : VerbDefinition
│   ├── RestartServicePreconditions.cs   # VerbPrecondition subtypes
│   └── RestartServicePostconditions.cs  # VerbPostcondition subtypes
├── ApplyConfigOverride/
│   └── …
```

One folder per verb keeps preconditions and postconditions colocated with
the verb definition they guard, which makes policy review tractable when a
verb is proposed for a new risk tier.

## Adding a new verb

1. Pick a `VerbId` (snake_case, unique across the fleet).
2. Create a folder under `Verbs/<PascalCaseVerbId>/`.
3. Define one record that inherits `VerbDefinition` with at minimum:
   - `VerbId` — matches folder name, audit log key.
   - `Category` — `infrastructure | diagnostic | action | plan | autonomy | consent | security`.
   - `Preconditions` — concrete `VerbPrecondition` records.
   - `Postconditions` — concrete `VerbPostcondition` records.
   - `RequiresApproval` — `true` for any `HIGH` risk tier, `true` for any
     verb whose `BaaScope` is `BaaAmendment(…)`, `false` only for verbs
     whose blast radius is fully constrained by the charter tolerance.
4. Preconditions MUST fail closed — if the expression cannot be evaluated,
   the verb MUST NOT execute.
5. Write tests FIRST in `tests/SuavoAgent.Core.Tests/ActionGrammar/Verbs/`.
   TDD is enforced (`~/.claude/rules/common-testing.md`).
6. Do NOT wire the verb into host startup until the Mission Loop gate
   `mission-loop.phase1.enabled` flips in `config-overrides.json`.

## Don'ts

- Do NOT let a verb run `cmd.exe` / `powershell.exe` / `sh` with an
  unescaped parameter. Every parameter is typed and validated.
- Do NOT implement a verb that both mutates on-box state AND
  `RequiresApproval == false` without an invariants review (see
  `docs/self-healing/invariants.md`).
- Do NOT swallow exceptions inside `Execute`. The dispatcher relies on
  observable failures for the verify/rollback path.

## Related

- `docs/self-healing/action-grammar-v1.md` — full contract and rollout plan
- `docs/self-healing/invariants.md` — invariants every verb must respect
- `docs/self-healing/audit-schema.md` — the audit events verbs emit
