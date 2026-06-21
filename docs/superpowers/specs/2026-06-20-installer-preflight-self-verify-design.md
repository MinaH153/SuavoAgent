# Installer Preflight + Self-Verify — Design

**Date:** 2026-06-20
**Origin:** Nadim/DIB first on-site install. The GUI reported "install complete" while the agent was actually broken — the on-device brain could not load (missing `vcruntime140_1.dll` / VC++ 2015-2022 x64 Redistributable), and nothing in the installer caught it. A fresh pharmacy Windows box lacks runtime dependencies the agent silently assumes.

**Goal:** No install is ever reported "complete" unless the agent is provably working end-to-end. The installer must (1) **preflight** required dependencies and install the ones it can, and (2) **self-verify** the live agent (brain loads, pipe up, cloud auth) before the success screen — and fail loudly with the exact remediation if any gate fails.

**Tech stack:** existing `SuavoAgent.Setup` (GUI + ConsoleInstaller), the running Core/Helper services, the heartbeat/diagnostics snapshot already built (`DiagnosticsSnapshotBuilder`).

## Global constraints
- HIPAA: preflight/verify must read NO patient data — only service state, file presence, dependency versions, and connectivity booleans.
- Stealth: verification must NOT require provisioning vendor-visible access (no SQL login prompts). SQL reachability is checked passively only when the pharmacy has explicitly chosen SqlFirst; the default UiaFirst path verifies via UI-attach reachability, not DB credentials.
- Low-spec box: brain load can take seconds on a 2-core CPU — verify gates must use generous timeouts (brain load ≤ 60s) and show progress, never a spinner that looks hung.

## Problem (what went wrong, concretely)
1. `vcruntime140_1.dll` absent → LLamaSharp `NativeApi` static initializer throws → Tier-2 inference hard-down → every cloud chat fell to the canned reply. The installer never checked for VC++ Redist.
2. The success screen ("installed and ready") was driven by *file-copy completion*, not by *the agent actually working*. Dashboard likewise showed "brain · online" while inference was dead.
3. No single place reported "here is the layer that's broken and how to fix it" — it took a manual on-box Claude diagnostic to find it.

## Design

### Phase A — Preflight (before service install)
A `PreflightChecker` runs the checks below; each returns `{ id, status: ok|fixable|blocked, detail, remediation }`.

| Check | How | Action on fail |
|---|---|---|
| VC++ 2015-2022 x64 Redistributable | registry `HKLM\...\VC,redist.x64` + presence of `vcruntime140.dll`, `vcruntime140_1.dll`, `msvcp140.dll` in System32 | **auto-install**: bundled `vc_redist.x64.exe /quiet` (preferred — offline) or `winget install Microsoft.VCRedist.2015+.x64`; re-check |
| .NET runtime (target) | `dotnet --list-runtimes` / bundled self-contained | block with download link if missing |
| OS / arch | x64, Win10+ | block |
| Disk space | ≥ 3 GB free at install + data dirs | block |
| Native brain libs present | `llama.dll`, `ggml.dll`, `ggml-base.dll`, `ggml-cpu.dll` at NativeLibraryPath | block (packaging bug) |
| CPU AVX capability vs deployed lib variant | `__cpuid` AVX/AVX2/AVX512 flags vs the avx/avx2/noavx variant being installed | **select matching variant** (or noavx fallback); never deploy an AVX2 build to a non-AVX2 CPU |

`vc_redist.x64.exe` ships **inside the installer** so a box with no internet still gets the runtime. Preflight results render as a checklist screen; "blocked" items stop the install with copy-paste remediation.

### Phase B — Self-Verify (after service install, before Success screen)
A `PostInstallVerifier` drives the just-installed agent and gates the success screen on real behavior. It reuses the heartbeat/diagnostics path.

| Gate | Proof | Timeout |
|---|---|---|
| Services running | Core, Helper, Broker, Watchdog all `Running` | 30s |
| Core↔Helper pipe | a `ping` command round-trips Core→Helper→Core | 15s |
| **Brain loads** | trigger a one-token local inference (or read the startup log for `model loaded … SUCCESS` + no `NativeApi`/`TypeInitializationException`) | 60s |
| Cloud auth | heartbeat returns 200 (not 401); record ruleset 503 as a non-blocking warning | 30s |
| PMS reachability (modality-aware) | UiaFirst: can enumerate the PioneerRx top-level window when open (warn, not block, if PioneerRx closed). SqlFirst (opt-in only): TCP+TLS+auth probe with the configured credentials | 30s |

**Success screen only shows if all blocking gates pass.** Any failure shows the exact failing layer + remediation and offers "retry verify" / "view log" / "get help." Write a `install-verify.json` summary next to `setup.log` for support.

### Phase C — Surfacing
- The dashboard "Your computers & tools" rail must read the SAME verify summary, so cloud status can never claim healthy while a gate failed (kills the "brain online while dead" lie).
- The Success screen lists each gate with ✓/✗ so the operator sees proof, not a hollow "complete."

## Out of scope
- Auto-provisioning SQL access (violates stealth; SqlFirst stays an explicit opt-in).
- Remote-assist UX (separate onboarding spec).

## Testable deliverables
1. `PreflightChecker` unit-tested per check (mock registry/file/cpuid); VC++ branch installs the bundled redist and re-passes.
2. Bundled `vc_redist.x64.exe` present in the installer payload; offline box install makes the brain load.
3. `PostInstallVerifier` gates the Success screen; a box with the brain forced-broken shows the brain gate ✗ with VC++ remediation and does NOT show "complete."
4. Dashboard rail reads `install-verify.json`; forced gate failure shows red, not "online."
