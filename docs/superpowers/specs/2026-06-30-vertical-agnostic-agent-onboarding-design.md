# Design: Vertical-agnostic agent onboarding (2026-06-30)

## Goal & scope
De-hardcode SuavoAgent's onboarding (cloud wizard **and** .NET install) from
pharmacy/PioneerRx so a non-pharmacy business can onboard. **Approach A,
research-refined:** the cloud is the single source of truth for a business's
`vertical` + compliance policy; the agent renders consent + picks a connector
from a **signed, read-only** config; real PHI/PAN protection stays **server-side
and independent of the agent's claimed vertical**.

This slice ships exactly **two concrete vertical configs** — `pharmacy` (HIPAA +
PioneerRx, today's behavior, no longer hardcoded) and `default` (no PMS,
observe-only, `complianceMode: none`). **No speculative restaurant/POS connector**
(YAGNI — that's a later slice with a real second vertical's agent job). The win:
a non-pharmacy business completes onboarding without hitting NPI/HIPAA/PioneerRx
walls, and the extension point is obvious.

## The 5 correctness rules (load-bearing — from the compliance research)
1. **Cloud is sole authority** for `vertical` + `complianceMode`, derived
   server-side from the authenticated business record, delivered **signed** in
   the pairing/register payload. Agent verifies the signature and treats it
   **read-only — no env/registry/CLI override path** to weaken it.
2. **Fail CLOSED to HIPAA** when config is absent/stale/unknown: scrubbing on,
   egress gate denies, PHI-actuation blocked. NEVER fail to `none`. (Unknown on a
   restaurant just shows extra consent; unknown failing to `none` on a pharmacy
   leaks PHI.) The agent rejects a `complianceMode` weaker than its last-known-
   good (downgrade refusal, TLS-style).
3. **Enforcement is server-side.** The deny-by-default egress gate
   (`containsPatientPhi`) + the scrubber are the control of record, independent
   of the agent's self-asserted vertical. Agent-side consent/connector = UX. A
   covered-entity record can NEVER be served `none`.
4. **Compliance mapping = cloud DATA; connectors = agent CODE** behind
   `ISystemConnector`. Patch a misclassified vertical without redeploying boxes.
5. **BAA per covered-entity relationship only** (pharmacy yes; restaurant no — no
   PHI, no covered entity, no BAA). The install consent is a **durable, tamper-
   evident audit record** referencing the executed BAA version/date — not just a
   local checkbox.

## Compliance model (HHS/PCI-grounded)
- `complianceMode: hipaa` → BA relationship; install consent surfaces + records
  the BAA acknowledgment (operator identity, workstation, BAA version, minimum-
  necessary scope) to the cloud audit log. PHI scrubber + egress gate active.
- `complianceMode: pci` → **PAN-masking scrubber on + observe-only**; a "we do
  not capture cardholder data; do not point this at PAN-bearing screens" notice.
  NOT a HIPAA-style legal gate, NOT a "PCI compliant" claim. (Screen-scraping is
  the PCI trap — PAN can surface in a capture; mask by default.) *No `pci` vertical
  ships this slice, but the mode is defined so retail slots in.*
- `complianceMode: none` → minimal acknowledgment (Terms/Privacy); observe-only.

## Architecture

### Vertical config (cloud data — the single source of truth)
A `vertical` value on the business profile (the signup `intent` already carries
this: `pharmacy`/`restaurant`/…). A server-side map `vertical → VerticalConfig`:
```
VerticalConfig {
  vertical: string                 // "pharmacy" | "default" (this slice)
  complianceMode: "hipaa"|"pci"|"none"
  systemConnector: "pioneerrx"|"none"
  connectorLabel: string           // "PioneerRx" | "(no system)"
  redactionProfileId: string       // "phi-v1" | "none"
  framing: { productNoun, systemNoun, ... }  // copy tokens, no "pharmacy"/"Rx" hardcoded
  compliance: { baaRequired: bool, consentCopyId: string }
}
```
Lives in cloud code as a typed map (not a DB table yet — YAGNI; promote to a
table when verticals are user-configurable). Pharmacy + default defined now.

### Delivery (signed, server-authoritative)
- The **wizard** (server components) reads the business's `VerticalConfig`
  directly (same-process, trusted) to render steps/copy/consent.
- The **agent** gets the config at **`/api/agent/register`** (and device-token),
  inside a **signed** `verticalConfig` claim (reuse the manifest-signing key /
  the existing `SIGNING_KEY_PEM` ECDSA path the OTA manifest uses). The agent
  verifies the signature before honoring it. Over TLS + signed = tamper-evident.
  *(If signing the register payload is too invasive for v1, ship server-
  authoritative-over-TLS first and add the signature as a fast-follow — but the
  agent must still fail-closed-to-HIPAA on anything it can't verify.)*

### Cloud wizard (`src/components/pharmacy/onboarding/*` → vertical-aware)
- Rename the dir conceptually to agent-onboarding (or keep path, de-pharmacy the
  contents). Steps become config-driven:
  - `step-authorization` (HIPAA BA ack): shown when `complianceMode==hipaa`;
    PCI-notice variant when `pci`; minimal Terms ack when `none`. Copy from
    `consentCopyId`.
  - `step-connect-pms` / `step-sql-server`: shown when
    `systemConnector=="pioneerrx"`; for `none`, replaced by a one-line "the agent
    will observe your workflow" (no system to connect).
  - `step-download-agent`: already canonical (token-in-filename) — framing copy
    from `framing` (no hardcoded "PioneerRx"/"pharmacy").
- The wizard reads `VerticalConfig` once at the top and passes it to steps.

### .NET install (`src/SuavoAgent.Setup/*` → vertical-aware)
- **`ISystemConnector`** (new): `Probe()/Capabilities()`, `Discover()` (the
  PioneerRx SQL auto-discovery), `RedactionProfile`. Implementations:
  - `PioneerRxConnector` — today's PioneerRx detection + `PioneerPharmacy.exe.config`
    SQL discovery, refactored behind the interface.
  - `NullConnector` — the existing "no-PMS observe mode" as a first-class
    connector (observe-only, no discovery).
- The install picks the connector by `verticalConfig.systemConnector` (from the
  signed config). Unknown/absent → fail closed: HIPAA posture + `PioneerRx`? No —
  fail-closed means **strictest compliance**, but connector selection on unknown
  → treat as no-trusted-config → block PHI actuation + require a valid config
  (don't silently run PioneerRx without a verified pharmacy config).
- **Consent (`ConsentView`)**: render from `complianceMode` — HIPAA BA ack
  (pharmacy) vs PCI notice vs minimal. Today's HIPAA consent becomes the `hipaa`
  branch. Record the consent (with BAA version) to the cloud audit log, not just
  locally.
- The agent **never computes `complianceMode`** from what it observes locally.

### Server-side enforcement (unchanged control of record)
The egress gate (`containsPatientPhi`, deny-by-default) + scrubber stay
server-side and vertical-independent. For `pci`, the redaction profile masks
PAN; for `hipaa`, PHI. The agent's claimed vertical never relaxes the gate.

## Error handling / fail-safe
- Config fetch fails / vertical unknown / signature invalid → **HIPAA posture,
  PHI actuation blocked**, installer shows "couldn't verify your account
  configuration — contact support" rather than proceeding `none`.
- Downgrade: agent refuses a `complianceMode` weaker than last-known-good.
- Connector probe failure (e.g., PioneerRx not found on a pharmacy box) → today's
  "no-PMS mode" path, still under HIPAA posture (it's a pharmacy).

## Testing
- Cloud: VerticalConfig map (pharmacy→hipaa/pioneerrx, default→none/none);
  wizard renders the right consent/steps per mode; `/register` returns the signed
  verticalConfig; signature verification.
- Agent: `ISystemConnector` selection by config; `PioneerRxConnector` parity with
  current detection/discovery; `NullConnector` observe-only; consent renders per
  mode; **fail-closed-to-HIPAA on absent/unknown/invalid-signature**; downgrade
  refusal. Full `dotnet test` green.
- E2E (on-box, owed): pharmacy install = today's HIPAA + PioneerRx flow
  unchanged; a `default`-vertical install = no NPI/HIPAA/PioneerRx, observe mode.

## Out of scope (this slice)
- A real restaurant/POS connector + its agent job (needs a concrete second
  vertical — separate slice).
- Promoting VerticalConfig to a DB table / user-configurable verticals.
- Upstream signup NPI-verification vertical-awareness (the signup funnel already
  branches on `intent`; the NPI step is only reached for pharmacy).
- HITRUST/PCI formal certification (SOC2 baseline + per-tenant overlays is the
  posture; PCI controls added only when a card-touching vertical ships).

## Key files (where to hook)
- Cloud: `src/components/pharmacy/onboarding/*`, `src/app/api/agent/register/route.ts`
  (+ `device-token`), the egress gate (`containsPatientPhi`), `src/lib/` for the
  new `vertical-config.ts`.
- Agent: `src/SuavoAgent.Setup/SetupConfig.cs`, `ConsoleInstaller.cs` (PioneerRx
  detection + SQL discovery → `PioneerRxConnector`), `Gui/Views/ConsentView.axaml`
  + `ConsentViewModel.cs`, new `ISystemConnector.cs`.

## Sources (compliance grounding)
HHS Business Associates FAQ + Cloud Computing guidance (BAA = PHI relationship;
no-view ≠ exempt FAQ 2076; conduit exception FAQ 2077). PCI SSC scoping &
segmentation (store/process/transmit OR impact CDE; screen-capture trap). NIST
zero-trust (never trust the client to enforce). CrowdStrike/Datadog/LaunchDarkly
(policy = cloud data, connectors = code; server-side eval for security-sensitive
flags). *Verify HHS FAQ 2076/2077 in a browser before this goes in a compliance
artifact — HHS blocks automated fetch.*
