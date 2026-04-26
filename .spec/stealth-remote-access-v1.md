# Stealth Remote Access — v1 design spec

## What we're building

A way for Joshua (and eventually one or two MKM ops people) to remotely troubleshoot a pharmacy install **without driving to the customer**. Industry-standard means:

- Cloud-mediated. No inbound firewall punching at the pharmacy. The agent dials out; Joshua dials in to the cloud; cloud bridges them.
- Encrypted end-to-end. Cloud never sees plaintext PHI; relay-only.
- Time-bounded sessions. Auto-disconnect on idle, hard cap, signed authorization per-session.
- Stealth. No popup at the pharmacy that pulls the pharmacist out of their workflow. The agent runs as a service; remote control is invisible at the local machine level (cursor doesn't move on the local screen unless explicitly allowed).
- HIPAA-grade audit. Every action — connect, screen view, command send, file pull, disconnect — chained-audit logged.
- Optional session recording for incident review.
- Pre-authorized via the BAA addendum (one-time customer consent at install time covers all future support sessions).

## What's already in place

- `/api/agent/commands` HMAC + ECDSA-signed command envelope (we ship signed `decommission`, `update`, `run_pricing_job`, `find_and_run_pricing_job` today). This is the right authorization channel.
- `IpcCommandServer.HandleCaptureScreenAsync` already exists (Helper side, currently no Core caller — see PR #26 contract docs).
- `EncryptedScreenStore` already encrypts captures at rest with operator-controlled retention.
- `PhiScrubbingExtractor` already scrubs OCR output.
- Chained audit log in Core's `AgentStateDb`.

## What's missing

1. **Real-time screen stream** (WebRTC or polled-capture fallback) — the agent has to push screen frames continuously, not on demand
2. **Input injection back to agent** (mouse + keyboard from Joshua's browser → agent → desktop) under signed authorization
3. **Cloud-side relay endpoint** — WebRTC signaling server + TURN if needed
4. **Browser-side viewer** — Joshua's UI for connect / view / send input / disconnect / record
5. **Session lifecycle** — start, heartbeat, idle-revoke, hard-cap revoke, recording upload
6. **Audit + consent surface** — every session logged in `support_sessions` (existing table) with chained audit entries

## Architecture — 3 layers

### Layer 1 — Authorization (already mostly in place)

Reuse `/api/admin/break-glass/request` flow:
- Joshua selects pharmacy from `/admin/customers`
- Picks reason `pilot_install` (or new reason `remote_troubleshoot`) + 30-char justification
- 2h time-bounded session created in `support_sessions`
- Audit entry written to `audit_log` with `action = 'remote_session.invoked'`
- Customer notice sent (or skipped per BAA addendum for `pilot_install` / `remote_troubleshoot`)

A new break-glass reason `remote_troubleshoot` with `requires_customer_notice = true` (one notice per session) keeps the HIPAA posture clean. For pre-authorized always-on access (e.g. emergencies after the BAA is signed), `requires_customer_notice = false` only with operator-signed `pilot_install`-style consent.

**This layer is 80% built.** Migration adds the new reason + the support session route already handles 2h cap + idle revoke.

### Layer 2 — Realtime screen + input (the hard part)

Two options, build both as fallbacks:

**Primary path: WebRTC**
- Agent's Helper process exposes a local WebRTC peer endpoint (use Microsoft's [WebRTC for .NET](https://github.com/microsoft/MixedReality-WebRTC) or [SIPSorcery](https://github.com/sipsorcery-org/sipsorcery))
- Cloud runs the signaling server: agent sends ICE candidates over signed-command channel; Joshua's browser receives them via Server-Sent Events
- TURN server only needed if pharmacy's NAT is symmetric (shouldn't be common; default to STUN-only)
- Stream codec: H.264 hardware-encoded if the pharmacy GPU supports it (Intel QuickSync usually does), VP8 fallback
- Resolution: capped at 1920x1080, 5fps default (industry standard for support — full motion isn't needed)

**Fallback path: signed-command capture polling**
- Joshua's browser sends signed `capture_screen` commands every 2s
- Agent returns scrubbed `ScreenFrame` + storage id (already implemented)
- Browser renders frames as a stop-motion sequence
- Input via `inject_input` signed command (mouse coords + keys)
- 2 fps is enough for "is the right window open" troubleshooting; not enough for fluid interaction
- Pros: works through any firewall, no WebRTC setup, audit-trail-native
- Cons: 2 fps UX; latency

Build the fallback path first (1 week). It unblocks "Joshua can see Nadim's screen from home." WebRTC layer is v1.1 (2-3 weeks more) for full interactive support.

### Layer 3 — Browser UI (the visible deliverable)

Single-page React component at `/admin/remote/<session_id>`:

- Live screen view (image element refreshing every 2s OR WebRTC `<video>` element)
- Session timer (counts down from 2h cap, idle warning at 14 min)
- Action panel: Restart Helper, Pull Logs, Force Reinstall, Run PIAG-1
- Recording toggle (default ON, recordings encrypted in Supabase Storage)
- Disconnect button → revokes session + writes audit
- HIPAA banner: "PHI may be visible. Session recorded. Logged to audit chain."

Reuses existing `/admin/break-glass/page.tsx` UI patterns + adds the realtime view.

## Stealth requirements — where the line is

What stealth means here:
- **No popup at the pharmacy** when the session connects (industry support tools default to consent popups; we replace those with the BAA pre-authorization)
- **No cursor movement on the local screen** by default — Joshua views, doesn't drive. Toggle to "drive mode" requires a second signed command + audit entry per drive-session.
- **No system tray notification** of an active remote session by default. Pharmacist can't tell from local UI.
- **Full audit trail** that Joshua used the session, what he saw, what commands he sent.

What stealth does NOT mean:
- **Not "covert from the customer"** — the BAA addendum has to disclose remote support capability up front. The pharmacist just doesn't see a popup mid-shift; the customer KNOWS this exists.
- **Not "no logs"** — every action is auditable for HIPAA + FDA review.
- **Not "no consent"** — pre-authorized via BAA, but per-session timestamps go in audit.

If Joshua wants something more covert than this (e.g. genuinely zero customer awareness), the legal posture would not survive a HIPAA audit. The model above is the maximum-stealth that HIPAA-defends.

## Build sequence — 4 PRs over 2 weeks

### PR 1 — `remote_troubleshoot` break-glass reason + support_sessions extension (cloud, half day)

- Migration: add `remote_troubleshoot` to `break_glass_reasons` (mirror of `pilot_install` PR #186)
- Add `support_sessions.session_kind` enum (`break_glass` | `remote_troubleshoot`)
- TS validator sync (same pattern as PR #186 follow-on)
- Tests pinning the new reason + kind

### PR 2 — Polled capture path (signed `capture_screen_for_support` + browser viewer) (~1 week)

- Cloud: new signed command `capture_screen_for_support` issued from `/admin/remote/<session_id>` UI
- Cloud: new endpoint `GET /api/admin/remote/<session_id>/last-capture` returns latest captured frame URL
- Agent: HandleCaptureScreenAsync already exists — wire it up via the signed command path
- Agent: append `vision_capture` chained audit per the contract from PR #26
- Browser: simple SPA showing the latest frame, refreshing every 2s
- Browser: Disconnect button calls existing break-glass revoke

### PR 3 — Action panel (`pull_logs`, `restart_helper`, `run_piag`) (~half week)

- Reuse signed-command pattern for new operational commands
- Logs returned as text (scrubbed); restart fires Watchdog auto-restart; run_piag invokes the PIAG-1 spec
- Each command writes a chained audit entry
- Browser action buttons + result display

### PR 4 — WebRTC upgrade path (v1.1, ~2 weeks separate)

- Real-time stream via SIPSorcery WebRTC peer
- Input injection via `inject_input` signed command
- Recording upload to Supabase Storage
- Drive-mode toggle with second-signature requirement

## Open questions for Joshua

1. **Customer notice at session start, or pre-authorized only via BAA?** "Pre-authorized" is more friction-free for ops but requires an explicit BAA addendum clause. "Per-session notice" is more visible but needs SendGrid + Twilio infra (already exists from break-glass).

2. **Drive mode: second-signature gate or single sign-on?** Industry-standard for healthcare is dual-signature for any keyboard/mouse injection. Adds 30s of friction per session, but makes the audit trail unimpeachable.

3. **Recording: default ON or OFF?** ON gives bulletproof incident review but storage cost grows fast. Suggest: ON, 30-day retention, encrypted in Supabase Storage with sponsor-only access.

4. **Should v1 ship the polled-capture path only, or wait for WebRTC?** Polled-capture unblocks "Joshua can see screen from home" in 1 week. WebRTC adds 2-3 weeks for fluid interactive support. I recommend ship polled, then add WebRTC.

5. **Who can use it besides Joshua?** Just Joshua for now? Or wire in a "support_engineer" role? RBAC question — affects PR 1's session_kind + actor_id columns.

## What this is NOT

- Not a screen-share product for end users. Internal ops only.
- Not a replacement for going onsite for hardware issues.
- Not a substitute for PIAG-1 — the install acceptance gate runs ON every install; remote troubleshooting is the human-driven escalation path when PIAG-1 catches a failure.
- Not a vendor remote-support product (TeamViewer / AnyDesk style). We're building this in-house specifically because the existing vendors don't cleanly fit HIPAA + our existing audit chain + our pharmacy-specific commands.

## Acceptance criteria for v1 (polled-capture path)

- Joshua can open `/admin/remote/<session_id>` from any laptop
- Within 5 seconds, sees the live (~2 fps) screen of the pharmacy PC
- Can pull recent Helper logs without leaving the page
- Can restart Helper or trigger PIAG-1
- All actions appear in `audit_log` within 1 second of execution
- Session auto-revokes after 2h or 15 min of idle
- Pharmacist sees no popup, no cursor movement, no tray notification
- HIPAA dossier: every byte that left the pharmacy was scrubbed via PhiScrubbingExtractor

## Pre-build prerequisites (already done)

- ✅ `/api/agent/commands` signed-command envelope (existing)
- ✅ `IpcCommandServer.HandleCaptureScreenAsync` (existing)
- ✅ `EncryptedScreenStore` + `PhiScrubbingExtractor` (existing)
- ✅ `support_sessions` table with idle-expiry + 2h cap (existing)
- ✅ `audit_log` chain (existing)
- ✅ `pilot_install` break-glass reason as the precedent for `remote_troubleshoot` (PR #186)
- ✅ Helper resource self-kill guard (PR #25 — protects pharmacy PC even during a long support session)
- ✅ Chained audit on PHI pull (PR #26 — closes the audit gap before remote sessions can read PHI)

## Estimated effort

- PR 1: 4 hours
- PR 2: 1 week (40h)
- PR 3: 1.5 days (12h)
- PR 4 (v1.1 WebRTC): 2 weeks (80h)

Total v1 (polled capture, no WebRTC): ~1.5 weeks.
Total v1.1 (full interactive): +2 weeks.
