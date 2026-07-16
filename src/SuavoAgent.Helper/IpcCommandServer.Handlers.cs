using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Serilog;
using SuavoAgent.Contracts.Discovery;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Contracts.Security;
using SuavoAgent.Core.Discovery;
using SuavoAgent.Helper.Actuation;
using SuavoAgent.Helper.IntentCursor;
using SuavoAgent.Helper.Vision;
using SuavoAgent.Helper.Workflows;

namespace SuavoAgent.Helper;

public sealed partial class IpcCommandServer
{
    private Task<IpcResponse> DispatchAsync(IpcRequest request, CancellationToken ct)
    {
        return request.Command switch
        {
            IpcCommands.VisionStateHandshake => Task.FromResult(
                HandleVisionStateHandshake(request)),
            IpcCommands.PricingLookup => HandlePricingLookupAsync(request, ct),
            IpcCommands.PioneerRxTop500Export => HandlePioneerRxTop500ExportAsync(request, ct),
            IpcCommands.PioneerRxTop500ReadArtifact => HandlePioneerRxTop500ReadArtifactAsync(request, ct),
            IpcCommands.PioneerRxPricedWorkbookBegin =>
                HandlePioneerRxPricedWorkbookBeginAsync(request, ct),
            IpcCommands.PioneerRxPricedWorkbookChunk =>
                HandlePioneerRxPricedWorkbookChunkAsync(request, ct),
            IpcCommands.PioneerRxPricedWorkbookCommit =>
                HandlePioneerRxPricedWorkbookCommitAsync(request, ct),
            IpcCommands.PricingObservationContext => Task.FromResult(
                HandlePricingObservationContext(request)),
            IpcCommands.CaptureScreen => HandleCaptureScreenAsync(request, ct),
            IpcCommands.FindFile when _allowNonPioneerRxCapabilities =>
                HandleFindFileAsync(request, ct),
            IpcCommands.FindFile => Task.FromResult(ScopeDenied(request)),
            IpcCommands.IntentCursor => HandleIntentCursorAsync(request, ct),
            "presence.set_visible" => Task.FromResult(HandlePresenceSetVisible(request)),
            IpcCommands.Ping => Task.FromResult(Ok(request.Id, request.Command,
                JsonSerializer.SerializeToElement(BuildPingInfo()))),
            ActuationIpcCommands.GetState
                or ActuationIpcCommands.ClickByLabel
                or ActuationIpcCommands.ClickBySignature
                or ActuationIpcCommands.TypeText
                or ActuationIpcCommands.PressKeys
                or ActuationIpcCommands.LaunchSandboxApp
                or ActuationIpcCommands.ReloadAllowlist
                or ActuationIpcCommands.AssertElement
                or ActuationIpcCommands.DiscoverElements
                when _allowNonPioneerRxCapabilities
                => HandleActuationAsync(request, ct),
            ActuationIpcCommands.GetState
                or ActuationIpcCommands.ClickByLabel
                or ActuationIpcCommands.ClickBySignature
                or ActuationIpcCommands.TypeText
                or ActuationIpcCommands.PressKeys
                or ActuationIpcCommands.LaunchSandboxApp
                or ActuationIpcCommands.ReloadAllowlist
                or ActuationIpcCommands.AssertElement
                or ActuationIpcCommands.DiscoverElements
                => Task.FromResult(ScopeDenied(request)),
            PioneerRxActuationIpcCommands.Click
                or PioneerRxActuationIpcCommands.TypeText
                or PioneerRxActuationIpcCommands.Query
                or PioneerRxActuationIpcCommands.WritebackRxDelivery
                => HandlePioneerRxActuationAsync(request, ct),
            _ => Task.FromResult(Error(request.Id, request.Command, "unknown_command", $"Unknown command: {request.Command}"))
        };
    }

    private static IpcResponse ScopeDenied(IpcRequest request) => Error(
        request.Id,
        request.Command,
        "observation_policy_scope_denied",
        "This capability is outside the activated PioneerRx-only policy.",
        IpcStatus.Forbidden);

    internal HelperPingInfo BuildPingInfo()
    {
        var session = HelperSessionProbe.Current();
        return session with { VisionRuntime = _visionRuntimeStatus?.Snapshot() };
    }

    private IpcResponse HandleVisionStateHandshake(IpcRequest request)
    {
        var result = _visionGenerationGate.VerifyAndLatch(request.Data);
        if (!result.Accepted)
        {
            _logger.Warning(
                "IpcCommandServer: vision state handshake rejected code={Code} localGeneration={Generation}",
                result.Code,
                _visionGenerationGate.LocalGeneration);
            return Error(
                request.Id,
                request.Command,
                result.Code,
                "Core and Helper vision state do not match.",
                IpcStatus.BadRequest);
        }
        return Ok(request.Id, request.Command, JsonSerializer.SerializeToElement(new
        {
            matched = true,
            generation = _visionGenerationGate.LocalGeneration,
            configDigest = _visionGenerationGate.LocalDigest,
            // The command pipe has authenticated Core before dispatch. This
            // closed PHI-free status is advisory here and authoritative again
            // in ping, where Core records it for local health/heartbeat.
            visionRuntime = _visionRuntimeStatus?.Snapshot(),
        }));
    }

    private IpcResponse HandlePricingObservationContext(IpcRequest request)
    {
        var context = _pricing.CaptureObservationContext();
        return context is null
            ? Error(
                request.Id,
                request.Command,
                "pricing_screen_identity_unavailable",
                "The live PioneerRx screen identity is unavailable.")
            : Ok(
                request.Id,
                request.Command,
                JsonSerializer.SerializeToElement(context));
    }

    /// <summary>Remote/dashboard instant-hide for the presence cursor. Visual-only;
    /// toggles CursorVisible on the shared preference store.</summary>
    private IpcResponse HandlePresenceSetVisible(IpcRequest request)
    {
        if (_presenceStore is null)
            return Error(request.Id, request.Command, "presence_unavailable", "Presence not configured");
        if (request.Data is null)
            return Error(request.Id, request.Command, "bad_request", "Missing data", IpcStatus.BadRequest);
        try
        {
            var visible = request.Data.Value.GetProperty("visible").GetBoolean();
            _presenceStore.SetVisible(visible);
            return Ok(request.Id, request.Command, JsonSerializer.SerializeToElement(new { visible }));
        }
        catch (Exception ex)
        {
            _logger.Warning(
                "presence.set_visible bad request ({ExceptionType})",
                ex.GetType().Name);
            return Error(request.Id, request.Command, "bad_request", "Invalid presence payload", IpcStatus.BadRequest);
        }
    }

    private async Task<IpcResponse> HandlePioneerRxActuationAsync(IpcRequest request, CancellationToken ct)
    {
        if (_pioneerRx is null)
        {
            return Error(request.Id, request.Command, "pioneerrx_unavailable",
                "Helper started without PioneerRxCommandHandler — drop pioneerrx.json into ProgramData\\SuavoAgent and restart");
        }
        try
        {
            var result = await _pioneerRx.HandleAsync(request.Command, request.Data, ct).ConfigureAwait(false);
            var payload = JsonSerializer.SerializeToElement(result);
            return Ok(request.Id, request.Command, payload);
        }
        catch (Exception ex)
        {
            _logger.Warning(
                "IpcCommandServer: pioneerrx dispatch failure for {Command} ({ExceptionType})",
                request.Command,
                ex.GetType().Name);
            return Error(
                request.Id,
                request.Command,
                "pioneerrx_dispatch_exception",
                "PioneerRx could not complete the approved local command.");
        }
    }

    private async Task<IpcResponse> HandleActuationAsync(IpcRequest request, CancellationToken ct)
    {
        if (_actuation is null)
        {
            return Error(request.Id, request.Command, "actuation_unavailable",
                "Helper started without ActuationCommandHandler — check Actuation:Enabled / SystemEnabled config");
        }

        try
        {
            if (request.Command == ActuationIpcCommands.GetState)
            {
                var state = _actuation.GetState();
                var json = JsonSerializer.SerializeToElement(state);
                return Ok(request.Id, request.Command, json);
            }

            var result = await _actuation.HandleAsync(request.Command, request.Data, ct).ConfigureAwait(false);
            var payload = JsonSerializer.SerializeToElement(result);
            // Always 200 at the IPC layer — the ActuationResult.Ok bit is the
            // semantic outcome. Cloud audit + WorkflowExecutor read .Ok / .RejectionCode.
            return Ok(request.Id, request.Command, payload);
        }
        catch (Exception ex)
        {
            _logger.Warning(
                "IpcCommandServer: actuation dispatch failure for {Command} ({ExceptionType})",
                request.Command,
                ex.GetType().Name);
            return Error(
                request.Id,
                request.Command,
                "actuation_dispatch_exception",
                "The approved local action could not be completed.");
        }
    }

    private async Task<IpcResponse> HandleIntentCursorAsync(IpcRequest request, CancellationToken ct)
    {
        if (_intentCursor is null)
        {
            return Error(request.Id, request.Command, "intent_cursor_unavailable",
                "Intent cursor not configured in this Helper instance");
        }

        if (request.Data is null)
        {
            return Error(request.Id, request.Command, "bad_request", "Missing data", IpcStatus.BadRequest);
        }

        IntentCursorRequest? cursorReq;
        try
        {
            cursorReq = JsonSerializer.Deserialize<IntentCursorRequest>(request.Data.Value);
        }
        catch (Exception ex)
        {
            _logger.Warning(
                "IpcCommandServer: intent_cursor bad request ({ExceptionType})",
                ex.GetType().Name);
            return Error(request.Id, request.Command, "bad_request", "Could not deserialize intent cursor request",
                IpcStatus.BadRequest);
        }

        var result = await _intentCursor.ShowAsync(cursorReq!, ct).ConfigureAwait(false);
        if (!result.Accepted || result.Rendered is null)
        {
            return Error(request.Id, request.Command, result.ErrorCode ?? "intent_cursor_rejected",
                "Intent cursor request rejected", IpcStatus.BadRequest);
        }

        _logger.Information(
            "IpcCommandServer: intent_cursor shown — requestId={Id} duration={DurationMs}ms tone={Tone}",
            request.Id, result.Rendered.DurationMs, result.Rendered.Tone);

        var payload = JsonSerializer.SerializeToElement(new IntentCursorResponse(
            Shown: true,
            CoordinateSpace: IntentCursorCoordinateSpaces.Screen,
            DurationMs: result.Rendered.DurationMs,
            DiameterPx: result.Rendered.DiameterPx,
            Tone: result.Rendered.Tone));
        return Ok(request.Id, request.Command, payload);
    }

    // ------------------------------------------------------------------
    // capture_screen — Vision capture command exposed to Core.
    //
    // AUDIT CONTRACT: the audit chain lives in Core's encrypted state.db,
    // which Helper cannot reach across the process boundary. Therefore
    // every CALLER in Core that sends capture_screen MUST first call
    // _stateDb.AppendChainedAuditEntry with EventType = "vision_capture"
    // and include the requesterId + reason in the audit row. Helper's job
    // is just to execute the capture and ship the scrubbed frame back —
    // never raw PNG bytes (verified at the JsonSerializer call below).
    //
    // This handler also logs every dispatch for an in-process audit trail
    // even when no Core caller is wired (current state — capture_screen
    // is an unused command path as of 2026-04-26). When Core wires the
    // first caller, the cited contract MUST land in the same PR.
    //
    // Codex Vision/observation review 2026-04-26 flagged this gap.
    // ------------------------------------------------------------------
    private async Task<IpcResponse> HandleCaptureScreenAsync(IpcRequest request, CancellationToken ct)
    {
        // Optional routing token: targetProcess="sandbox" → WINDOW-SCOPED sandbox capture, which uses the
        // launch-established HWND and is INDEPENDENT of the PHI-vision opt-in (_vision is null on a non-PHI
        // box). Parsed BEFORE the _vision null-check so the sandbox path is reachable there. Absent/other →
        // the PMS/PHI cadence path below, byte-for-byte unchanged.
        string? targetProcess = null;
        if (request.Data is { } d
            && d.TryGetProperty("targetProcess", out var tpEl)
            && tpEl.ValueKind == JsonValueKind.String)
        {
            targetProcess = tpEl.GetString();
        }
        if (string.Equals(targetProcess, "sandbox", StringComparison.OrdinalIgnoreCase))
        {
            if (!_allowNonPioneerRxCapabilities) return ScopeDenied(request);
            return await HandleSandboxCaptureAsync(request, ct);
        }

        if (_vision == null)
        {
            return Error(request.Id, request.Command, "vision_unavailable",
                "Vision not configured in this Helper instance");
        }
        if (!_visionGenerationGate.IsMatched)
        {
            return Error(
                request.Id,
                request.Command,
                "vision_generation_unconfirmed",
                "Vision refused until Core and Helper prove the same configuration generation.");
        }

        // HIPAA gate: refuse capture when PMS is not the foreground window.
        // Closes the Codex 2026-04-26 review gap that flagged this handler
        // would otherwise capture whatever the user happened to be looking
        // at (Chrome, email, banking) when Core's worker fired its cadence.
        if (_isPmsForeground != null && !_isPmsForeground())
        {
            _logger.Information(
                "IpcCommandServer: capture_screen rejected — PMS not foreground (requestId={Id})",
                request.Id);
            return Error(request.Id, request.Command, "not_foreground",
                "Capture refused — PMS process is not the foreground window");
        }

        // Helper-side dispatch log — pairs with Core-side AppendChainedAuditEntry
        // (which the caller is contractually required to write before sending).
        _logger.Information(
            "IpcCommandServer: capture_screen dispatch — requestId={Id} (caller MUST have written chained audit entry)",
            request.Id);

        try
        {
            var result = await _vision.CaptureAndExtractAsync(ct);
            if (result == null)
            {
                _logger.Information(
                    "IpcCommandServer: capture_screen returned null — requestId={Id} (vision disabled, rate-limited, or capture error)",
                    request.Id);
                return Error(request.Id, request.Command, "capture_failed",
                    "Capture returned null — vision disabled, rate-limited, or capture error");
            }

            // Only the scrubbed ScreenFrame + storage id cross the IPC boundary.
            // Raw PNG bytes stayed inside the Helper and are already encrypted
            // on disk.
            _logger.Information(
                "IpcCommandServer: capture_screen success — requestId={Id} storageId={StorageId} elements={Elements}",
                request.Id, result.StorageId, result.Frame?.Elements?.Count ?? 0);

            var payload = JsonSerializer.SerializeToElement(new
            {
                storageId = result.StorageId,
                frame = result.Frame,
            });
            return Ok(request.Id, request.Command, payload);
        }
        catch (Exception ex)
        {
            _logger.Warning(
                "IpcCommandServer: capture_screen dispatch error — requestId={Id} errorType={ErrorType}",
                request.Id, ex.GetType().Name);
            return Error(request.Id, request.Command, "capture_error", "Capture failed");
        }
    }

    // ------------------------------------------------------------------
    // SANDBOX capture (explore_sandbox on a NON-PHI box). Captures ONLY the window the preceding
    // launch_sandbox_app established (window-scoped PrintWindow) — structurally cannot leak any other
    // window's pixels, so it skips the PMS-foreground gate (there is no PMS) yet stays HIPAA-safe:
    //   • _sandboxDriver.ActiveTargetHwnd is an allowlisted sandbox app's window (set by launch).
    //   • VisionBootstrap.TryBuildWindowSandbox hard-refuses if PioneerRx is installed (build-time gate).
    //   • the request-time foreground check is a best-effort "user is watching the sandbox" signal; the
    //     real isolation is PrintWindow reading only that HWND's pixels.
    // ------------------------------------------------------------------
    private async Task<IpcResponse> HandleSandboxCaptureAsync(IpcRequest request, CancellationToken ct)
    {
        if (_sandboxDriver == null)
        {
            return Error(request.Id, request.Command, "sandbox_driver_unavailable",
                "Helper started without an actuation/sandbox driver");
        }

        var targetHwnd = _sandboxDriver.ActiveTargetHwnd;
        if (targetHwnd == IntPtr.Zero)
        {
            _logger.Information(
                "IpcCommandServer: sandbox capture_screen — no active target (send launch_sandbox_app first) requestId={Id}",
                request.Id);
            return Error(request.Id, request.Command, "sandbox_not_launched",
                "No sandbox app target established — send launch_sandbox_app first");
        }

        // AUTHORITATIVE request-time validation (Codex Q1/Q3 fix, UWP-aware): resolve the EFFECTIVE app
        // behind the target window (drilling the ApplicationFrameHost → CoreWindow UWP case) and require it
        // to be an allowlisted sandbox app. This closes the WindowFocusManager fallback gap where launch
        // resolution could latch onto ANY new top-level window (a PMS / banking dialog) — its effective
        // process would not be allowlisted → refuse. We capture ONLY when the window provably hosts
        // calc/notepad/operator-authorized, never an arbitrary window.
        if (!TryResolveAllowlistedSandboxApp(targetHwnd, out var effectiveAppPid))
        {
            return Error(request.Id, request.Command, "target_not_allowlisted",
                "Sandbox capture refused — target window is not an allowlisted sandbox app");
        }

        // We deliberately do NOT REQUIRE the sandbox app to be foreground. PrintWindow is HWND-scoped — it
        // renders ONLY the allowlisted target window's own content (via WM_PRINT), even when the window is
        // occluded or not focused — so capturing it regardless of foreground leaks nothing (window-scoping +
        // allowlist + owner-PID re-check ARE the HIPAA guarantee; this path is also build-time-gated to
        // non-PHI boxes). Requiring foreground made explore fragile: a self-launched app the agent is driving
        // loses focus the moment a menu/dialog opens, or simply isn't frontmost, yielding spurious
        // no_perception. Foreground is now an advisory signal only (logged), never a refusal.
        if (!SuavoAgent.Helper.Actuation.SandboxWindowResolver.IsSandboxAppForeground(effectiveAppPid))
        {
            _logger.Debug(
                "IpcCommandServer: sandbox capture — target pid={Pid} not foreground; capturing window-scoped anyway (requestId={Id})",
                effectiveAppPid, request.Id);
        }

        // Build (and cache by HWND) the window-scoped capture controller. Rebuilt only when the target
        // window changes (a new launch) — avoids reconstructing EncryptedScreenStore on every perceive.
        ScreenCaptureController? sandboxVision;
        lock (_sandboxVisionLock)
        {
            // Cache key = (HWND, effective app PID): rebuild if EITHER changes, so the cached capturer's
            // expected-PID always equals the just-validated effective app PID (Codex: a same-HWND effective-PID
            // churn must not leave a stale expected PID that the capturer's TOCTOU check would spuriously reject).
            if (_sandboxVision == null || _sandboxVisionHwnd != targetHwnd || _sandboxVisionPid != effectiveAppPid)
            {
                _sandboxVision = VisionBootstrap.TryBuildWindowSandbox(targetHwnd, effectiveAppPid, _logger);
                _sandboxVisionHwnd = targetHwnd;
                _sandboxVisionPid = effectiveAppPid;
            }
            sandboxVision = _sandboxVision;
        }

        if (sandboxVision == null)
        {
            return Error(request.Id, request.Command, "sandbox_vision_unavailable",
                "Sandbox vision pipeline unavailable — PMS installed or non-Windows");
        }

        _logger.Information(
            "IpcCommandServer: sandbox capture_screen dispatch — hwnd=0x{Hwnd:X} appPid={Pid} requestId={Id}",
            targetHwnd.ToInt64(), effectiveAppPid, request.Id);

        try
        {
            var result = await sandboxVision.CaptureAndExtractAsync(ct);
            if (result == null)
            {
                return Error(request.Id, request.Command, "capture_failed",
                    "Sandbox capture returned null — rate-limited or PrintWindow failed");
            }

            _logger.Information(
                "IpcCommandServer: sandbox capture_screen success — requestId={Id} storageId={StorageId} elements={Elements}",
                request.Id, result.StorageId, result.Frame?.Elements?.Count ?? 0);

            var payload = JsonSerializer.SerializeToElement(new
            {
                storageId = result.StorageId,
                frame = result.Frame,
            });
            return Ok(request.Id, request.Command, payload);
        }
        catch (Exception ex)
        {
            _logger.Warning(
                "IpcCommandServer: sandbox capture_screen error — requestId={Id} errorType={ErrorType}",
                request.Id, ex.GetType().Name);
            return Error(request.Id, request.Command, "capture_error", "Capture failed");
        }
    }

    /// <summary>
    /// Resolves the EFFECTIVE app behind <paramref name="hwnd"/> (UWP-aware: drills ApplicationFrameHost →
    /// CoreWindow) and returns true with <paramref name="effectiveAppPid"/> set ONLY if that app is an
    /// allowlisted sandbox app (calc/notepad/operator-authorized, incl. packaged aliases like CalculatorApp).
    /// Fail-closed: any non-Windows / missing process / non-allowlisted result returns false. This is the
    /// authoritative HIPAA gate ensuring a window-scoped capture targets a real sandbox app, never a window
    /// the launch resolver mis-latched.
    /// </summary>
    private bool TryResolveAllowlistedSandboxApp(IntPtr hwnd, out int effectiveAppPid)
    {
        effectiveAppPid = 0;
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            var pid = SuavoAgent.Helper.Actuation.SandboxWindowResolver.EffectiveAppPid(hwnd);
            if (pid <= 0)
            {
                _logger.Information("IpcCommandServer: sandbox capture refused — could not resolve an app behind the target window");
                return false;
            }

            using var proc = System.Diagnostics.Process.GetProcessById(pid);
            var name = proc.ProcessName; // image base name, no ".exe" (e.g. "notepad", "CalculatorApp")
            var identity = SuavoAgent.Helper.Actuation.SandboxProcessTrustVerifier
                .VerifyResolvedProcess(pid, name);
            if (identity.Trusted &&
                SuavoAgent.Helper.Actuation.SandboxWindowResolver.IsAllowlistedSandboxProcess(name))
            {
                effectiveAppPid = pid;
                return true;
            }

            _logger.Warning(
                "IpcCommandServer: sandbox capture refused — effective app pid={Pid} process='{Name}' failed identity/allowlist ({Code})",
                pid, name, identity.Code);
            return false;
        }
        catch (Exception ex)
        {
            _logger.Warning(
                "IpcCommandServer: sandbox allowlist verification failed closed ({ExceptionType})",
                ex.GetType().Name);
            return false;
        }
    }

    private Task<IpcResponse> HandlePricingLookupAsync(IpcRequest request, CancellationToken ct)
    {
        if (!_visionGenerationGate.IsMatched)
        {
            return Task.FromResult(Error(
                request.Id,
                request.Command,
                "vision_generation_unconfirmed",
                "Pricing refused until Core and Helper prove the same vision configuration generation."));
        }
        if (request.Data == null)
            return Task.FromResult(Error(request.Id, request.Command, "bad_request", "Missing data"));

        NdcPricingRequest? pricingReq;
        try
        {
            pricingReq = JsonSerializer.Deserialize<NdcPricingRequest>(request.Data.Value);
        }
        catch (Exception)
        {
            return Task.FromResult(Error(
                request.Id,
                request.Command,
                "bad_request",
                "The pricing request was invalid.",
                IpcStatus.BadRequest));
        }

        if (pricingReq == null)
            return Task.FromResult(Error(request.Id, request.Command, "bad_request", "Could not deserialize NdcPricingRequest"));

        // UIA must run on this thread — it's already called from the pipe handler loop
        // which runs on a thread pool thread. FlaUI is fine with this as long as it's
        // single-threaded per automation instance (PricingWorkflow uses its own UIA2Automation).
        var result = _pricing.Lookup(pricingReq);
        var data = JsonSerializer.SerializeToElement(result);
        return Task.FromResult(Ok(request.Id, request.Command, data));
    }

    private async Task<IpcResponse> HandleFindFileAsync(IpcRequest request, CancellationToken ct)
    {
        if (_locator is null)
        {
            return Error(request.Id, request.Command, "locator_unavailable",
                "File discovery not configured on this agent.");
        }
        if (request.Data is null)
        {
            return Error(request.Id, request.Command, "bad_request", "Missing data");
        }

        FindFileRequest? findReq;
        try
        {
            findReq = JsonSerializer.Deserialize<FindFileRequest>(request.Data.Value);
        }
        catch (Exception)
        {
            return Error(
                request.Id,
                request.Command,
                "bad_request",
                "The file request was invalid.",
                IpcStatus.BadRequest);
        }
        if (findReq is null)
        {
            return Error(request.Id, request.Command, "bad_request",
                "Could not deserialize FindFileRequest");
        }

        try
        {
            var result = await _locator.LocateAsync(findReq.Spec, DateTimeOffset.UtcNow, ct);
            // FileDiscoveryResult carries raw FileCandidateSample entries (paths,
            // filenames) in its Best/Alternatives. That's fine on this side of
            // the boundary — Core consumes the result locally. The cloud upload
            // happens at HeartbeatWorker after Core re-scrubs / projects.
            var payload = JsonSerializer.SerializeToElement(result);
            return Ok(request.Id, request.Command, payload);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Error(request.Id, request.Command, "cancelled", "Cancelled");
        }
        catch (Exception ex)
        {
            _logger.Warning(
                "IpcCommandServer: find_file dispatch error ({ExceptionType})",
                ex.GetType().Name);
            return Error(
                request.Id,
                request.Command,
                "locate_error",
                "The local file search could not be completed.");
        }
    }

}
