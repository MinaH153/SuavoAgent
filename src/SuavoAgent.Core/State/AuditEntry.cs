namespace SuavoAgent.Core.State;

/// <summary>
/// One immutable row in the chained audit log.
///
/// CHAINED FIELDS (contribute to prev_hash chain):
///   TaskId, EventType, FromState, ToState, Trigger, timestamp
///
/// NON-CHAINED METADATA (Codex 2026-04-26 audit gap fix — recorded for
/// forensic reconstruction but not protected by the hash chain):
///   CommandId        — signed command nonce when triggered by ECDSA cmd
///   RequesterId      — caller identifier (worker name, signed-cmd actor)
///   RxNumber         — HMAC-hashed Rx number (never raw PHI)
///   Actor            — "system" | "human" | "scheduled" | "api" | "operator"
///   SourceComponent  — class/module that wrote the entry (e.g.
///                      "rx_detection_worker", "ipc_command_server")
///   CaptureReason    — short human-readable why-this-happened string
///   WindowTitleHash  — HMAC of foreground window title at capture time
///                      (Vision capture audits only)
///   ElementCount     — UIA element count for tree snapshots / captures
///   ScrubberVersion  — PhiScrubber version that scrubbed any text/output
///                      shipped from this audit context
///   StorageId        — link to encrypted screen file if Vision capture
///
/// Per Codex finding: keep the chained hash format unchanged so existing
/// rows still verify; the new fields are added via additive ALTER TABLE
/// migrations and live alongside the chain. A future chain-format bump
/// would include these in the hash, but that's a breaking change so we
/// add them as audit-only metadata for now.
/// </summary>
public record AuditEntry(
    string TaskId,
    string EventType,
    string FromState,
    string ToState,
    string Trigger,
    string? CommandId = null,
    string? RequesterId = null,
    string? RxNumber = null,
    string? Actor = null,
    string? SourceComponent = null,
    string? CaptureReason = null,
    string? WindowTitleHash = null,
    int? ElementCount = null,
    string? ScrubberVersion = null,
    string? StorageId = null);
