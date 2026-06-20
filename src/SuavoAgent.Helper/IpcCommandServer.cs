using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Serilog;
using SuavoAgent.Contracts.Discovery;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Core.Discovery;
using SuavoAgent.Helper.Actuation;
using SuavoAgent.Helper.IntentCursor;
using SuavoAgent.Helper.Vision;
using SuavoAgent.Helper.Workflows;

namespace SuavoAgent.Helper;

internal static class ProcessImageInterop
{
    // Same fix as IpcPipeServer.cs — MainModule needs PROCESS_VM_READ which fails
    // crossing user/SYSTEM security tokens. QueryFullProcessImageNameW only needs
    // PROCESS_QUERY_LIMITED_INFORMATION and works across boundaries.
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(IntPtr hProcess, uint dwFlags, [Out] StringBuilder lpExeName, ref uint lpdwSize);

    public static string? Get(uint processId)
    {
        var hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (hProcess == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            uint size = (uint)sb.Capacity;
            return QueryFullProcessImageNameW(hProcess, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally
        {
            CloseHandle(hProcess);
        }
    }
}

/// <summary>
/// Helper-side command server. Core connects to this pipe to push commands
/// (e.g. pricing_lookup) and receive results. Reverse direction of the main IPC pipe.
///
/// Security hardening mirrors <see cref="SuavoAgent.Core.Ipc.IpcPipeServer"/>:
///   - ACL restricts pipe to SYSTEM + LocalService + Interactive
///   - Client process name must be SuavoAgent.Core
///   - Client MainModule path must be under the Helper install root
/// Without this, any local process running as the same user could drive UIA
/// automation of PioneerRx.
/// </summary>
public sealed class IpcCommandServer : IDisposable
{
    private readonly string _pipeName;
    private readonly PricingWorkflow _pricing;
    private readonly ScreenCaptureController? _vision;
    private readonly FileLocatorService? _locator;
    private readonly Func<bool>? _isPmsForeground;
    private readonly IntentCursorController? _intentCursor;
    private readonly ActuationCommandHandler? _actuation;
    private readonly PioneerRxCommandHandler? _pioneerRx;
    // Source of the launch_sandbox_app target HWND/PID for the window-scoped sandbox capture path.
    private readonly SendInputDriver? _sandboxDriver;
    // Cached window-scoped sandbox capture controller, rebuilt only when the target HWND changes
    // (avoids reconstructing EncryptedScreenStore on every perceive). Guarded by _sandboxVisionLock.
    private ScreenCaptureController? _sandboxVision;
    private IntPtr _sandboxVisionHwnd = IntPtr.Zero;
    private int _sandboxVisionPid; // effective app PID the cached capturer was built for (part of the cache key)
    private readonly object _sandboxVisionLock = new();
    private readonly ILogger _logger;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;

    private readonly bool _relaxClientPathValidation;

    // ------------------------------------------------------------------
    // Dispatch wedge watchdog. This server is a SINGLE pipe instance with ONE sequential
    // connection handler, and several dispatches run synchronous UIA/COM work (pricing lookup,
    // actuation) that can hang FOREVER against a hung PMS or a torn-down session. A wedged
    // dispatch therefore strands the entire command pipe permanently while the process looks
    // alive — the exact "agent says healthy but the cursor never moves" failure. The watchdog
    // bounds every dispatch: past the ceiling we log FATAL and self-terminate, and the Broker's
    // 5s watch loop relaunches a clean Helper (self-amputation > permanent deafness).
    // Ceiling rationale: the longest LEGITIMATE dispatch is find_file (Core waits 60s) and a
    // slow UIA pricing lookup (Core waits 30s); 5 minutes is >4x the worst legitimate case, so
    // a watchdog fire is always a genuine wedge, and worst-case blind time before auto-recovery
    // is ~5 minutes instead of forever.
    // ------------------------------------------------------------------
    internal static readonly TimeSpan DispatchWedgeCeiling = TimeSpan.FromMinutes(5);
    internal const int WedgedDispatchExitCode = 86;
    private readonly Action _onWedgedDispatch;

    public IpcCommandServer(
        string pipeName,
        PricingWorkflow pricing,
        ILogger logger,
        ScreenCaptureController? vision = null,
        FileLocatorService? locator = null,
        Func<bool>? isPmsForeground = null,
        IntentCursorController? intentCursor = null,
        ActuationCommandHandler? actuation = null,
        PioneerRxCommandHandler? pioneerRx = null,
        SendInputDriver? sandboxDriver = null,
        bool relaxClientPathValidation = false,
        Action? onWedgedDispatch = null)
    {
        _pipeName = pipeName;
        _pricing = pricing;
        _relaxClientPathValidation = relaxClientPathValidation;
        _vision = vision;
        _locator = locator;
        // When provided, capture_screen returns a not_foreground error if
        // the predicate is false at dispatch time. Wired from Helper.Program
        // to () => ForegroundGuard.IsPidForeground(pioneer.ProcessId), so
        // an alt-tabbed user's Chrome / email / banking window is never
        // captured even with Vision.Enabled=true.
        _isPmsForeground = isPmsForeground;
        _intentCursor = intentCursor;
        _actuation = actuation;
        _pioneerRx = pioneerRx;
        _sandboxDriver = sandboxDriver;
        _logger = logger;
        _onWedgedDispatch = onWedgedDispatch ?? (() => Environment.Exit(WedgedDispatchExitCode));
    }

    public void Start(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _listenTask = ListenLoop(_cts.Token);
    }

    private async Task ListenLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = CreateSecurePipe(_pipeName);

                _logger.Debug("IpcCommandServer: waiting for Core on pipe {Name}", _pipeName);
                await pipe.WaitForConnectionAsync(ct);

                // Client verification happens INSIDE HandleConnection, after the first frame is read
                // and before that frame is dispatched. VerifyClientIsCore's primary identity proof
                // (token-SID via ImpersonateNamedPipeClient) only works once the server has read a
                // message from the pipe — verifying here (pre-read) would make the SID path silently
                // fall through to the image-path branch (which fails for the de-privileged Helper →
                // the command-pipe flap). Reading one bounded frame from an as-yet-unverified peer is
                // safe: the pipe ACL already restricts connectors, and NO command is dispatched until
                // verification passes.
                await HandleConnection(pipe, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "IpcCommandServer: connection error");
                await Task.Delay(1000, ct);
            }
            finally
            {
                pipe?.Dispose();
            }
        }
    }

    private async Task HandleConnection(NamedPipeServerStream pipe, CancellationToken ct)
    {
        var verified = false;
        while (!ct.IsCancellationRequested && pipe.IsConnected)
        {
            try
            {
                var json = await IpcFraming.ReadFrameAsync(pipe, ct);
                if (json == null) break;

                // Verify the peer ONCE — now that a frame has been read, ImpersonateNamedPipeClient
                // (the token-SID identity proof) works. Verification happens BEFORE the frame is
                // deserialized/dispatched, so no command from an unverified client is ever executed.
                if (!verified)
                {
                    if (!VerifyClientIsCore(pipe))
                    {
                        _logger.Warning("IpcCommandServer: client failed verification after first frame — disconnecting");
                        break;
                    }
                    verified = true;
                    _logger.Information("IpcCommandServer: Core connected + verified on pipe {Name}", _pipeName);
                }

                var request = JsonSerializer.Deserialize<IpcRequest>(json);
                if (request == null) continue;

                _logger.Debug("IpcCommandServer: received {Command} [{Id}]", request.Command, request.Id);

                var response = await DispatchGuardedAsync(request, ct);
                var responseJson = JsonSerializer.Serialize(response);
                await IpcFraming.WriteFrameAsync(pipe, responseJson, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (IOException)
            {
                _logger.Information("IpcCommandServer: Core disconnected");
                break;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "IpcCommandServer: message error");
            }
        }
    }

    /// <summary>
    /// Runs <see cref="DispatchAsync"/> under the wedge watchdog. On a wedge (dispatch exceeds
    /// <see cref="DispatchWedgeCeiling"/>): log FATAL and invoke the wedge action — in
    /// production that exits the process so the Broker relaunches a clean Helper within ~5s,
    /// freeing the single-instance command pipe a hung UIA/COM call would otherwise hold
    /// forever. The error response below is only reachable with an injected non-exiting
    /// action (tests) or in the narrow window before Exit tears the process down.
    /// </summary>
    private async Task<IpcResponse> DispatchGuardedAsync(IpcRequest request, CancellationToken ct)
    {
        var (wedged, response) = await AwaitWithWedgeGuard(
            DispatchAsync(request, ct), DispatchWedgeCeiling, _onWedgedDispatch).ConfigureAwait(false);
        if (!wedged) return response!;

        _logger.Fatal(
            "IpcCommandServer: dispatch of {Command} [{Id}] WEDGED — exceeded the {Ceiling} ceiling " +
            "(hung UIA/COM call). Self-terminating with exit code {ExitCode} so the Broker relaunches " +
            "a clean Helper and frees the command pipe.",
            request.Command, request.Id, DispatchWedgeCeiling, WedgedDispatchExitCode);
        return Error(request.Id, request.Command, "dispatch_wedged",
            $"Dispatch exceeded the {DispatchWedgeCeiling.TotalMinutes:F0}-minute wedge ceiling");
    }

    /// <summary>
    /// The wedge-guard mechanism, extracted pure-ish for unit tests: await the dispatch up to
    /// <paramref name="ceiling"/>; past it, invoke <paramref name="onWedged"/> (production:
    /// process exit) and report wedged=true. The abandoned task keeps running on its wedged
    /// thread — irrelevant in production (the process exits) and harmless in tests.
    /// </summary>
    internal static async Task<(bool Wedged, IpcResponse? Response)> AwaitWithWedgeGuard(
        Task<IpcResponse> dispatch, TimeSpan ceiling, Action onWedged)
    {
        try
        {
            return (false, await dispatch.WaitAsync(ceiling).ConfigureAwait(false));
        }
        catch (TimeoutException)
        {
            onWedged();
            return (true, null);
        }
    }

    private Task<IpcResponse> DispatchAsync(IpcRequest request, CancellationToken ct)
    {
        return request.Command switch
        {
            IpcCommands.PricingLookup => HandlePricingLookupAsync(request, ct),
            IpcCommands.CaptureScreen => HandleCaptureScreenAsync(request, ct),
            IpcCommands.FindFile => HandleFindFileAsync(request, ct),
            IpcCommands.IntentCursor => HandleIntentCursorAsync(request, ct),
            IpcCommands.Ping => Task.FromResult(Ok(request.Id, request.Command,
                JsonSerializer.SerializeToElement(HelperSessionProbe.Current()))),
            ActuationIpcCommands.GetState
                or ActuationIpcCommands.ClickByLabel
                or ActuationIpcCommands.TypeText
                or ActuationIpcCommands.PressKeys
                or ActuationIpcCommands.LaunchSandboxApp
                or ActuationIpcCommands.ReloadAllowlist
                or ActuationIpcCommands.AssertElement
                or ActuationIpcCommands.DiscoverElements
                => HandleActuationAsync(request, ct),
            PioneerRxActuationIpcCommands.Click
                or PioneerRxActuationIpcCommands.TypeText
                or PioneerRxActuationIpcCommands.Query
                or PioneerRxActuationIpcCommands.WritebackRxDelivery
                => HandlePioneerRxActuationAsync(request, ct),
            _ => Task.FromResult(Error(request.Id, request.Command, "unknown_command", $"Unknown command: {request.Command}"))
        };
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
            _logger.Warning(ex, "IpcCommandServer: pioneerrx dispatch failure for {Command}", request.Command);
            return Error(request.Id, request.Command, "pioneerrx_dispatch_exception", ex.Message);
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
            _logger.Warning(ex, "IpcCommandServer: actuation dispatch failure for {Command}", request.Command);
            return Error(request.Id, request.Command, "actuation_dispatch_exception", ex.Message);
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
            _logger.Warning(ex, "IpcCommandServer: intent_cursor bad request — requestId={Id}", request.Id);
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
            return await HandleSandboxCaptureAsync(request, ct);
        }

        if (_vision == null)
        {
            return Error(request.Id, request.Command, "vision_unavailable",
                "Vision not configured in this Helper instance");
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
            _logger.Warning(ex, "IpcCommandServer: capture_screen dispatch error — requestId={Id}", request.Id);
            return Error(request.Id, request.Command, "capture_error", ex.Message);
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
            _logger.Warning(ex, "IpcCommandServer: sandbox capture_screen error — requestId={Id}", request.Id);
            return Error(request.Id, request.Command, "capture_error", ex.Message);
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
            if (SuavoAgent.Helper.Actuation.SandboxWindowResolver.IsAllowlistedSandboxProcess(name))
            {
                effectiveAppPid = pid;
                return true;
            }

            _logger.Warning(
                "IpcCommandServer: sandbox capture refused — effective app pid={Pid} process='{Name}' is NOT an allowlisted sandbox app",
                pid, name);
            return false;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "IpcCommandServer: sandbox allowlist verification failed — refusing (fail-closed)");
            return false;
        }
    }

    private Task<IpcResponse> HandlePricingLookupAsync(IpcRequest request, CancellationToken ct)
    {
        if (request.Data == null)
            return Task.FromResult(Error(request.Id, request.Command, "bad_request", "Missing data"));

        NdcPricingRequest? pricingReq;
        try
        {
            pricingReq = JsonSerializer.Deserialize<NdcPricingRequest>(request.Data.Value);
        }
        catch (Exception ex)
        {
            return Task.FromResult(Error(request.Id, request.Command, "bad_request", ex.Message));
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
        catch (Exception ex)
        {
            return Error(request.Id, request.Command, "bad_request", ex.Message);
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
            _logger.Warning(ex, "IpcCommandServer: find_file dispatch error");
            return Error(request.Id, request.Command, "locate_error", ex.Message);
        }
    }

    private static IpcResponse Ok(string id, string command, System.Text.Json.JsonElement? data) =>
        new(id, IpcStatus.Ok, command, data, null);

    private static IpcResponse Error(string id, string command, string code, string message, int status = IpcStatus.InternalError) =>
        new(id, status, command, null,
            new IpcError(code, message, false, 1));

    /// <summary>
    /// Creates a named pipe restricted to SYSTEM + LocalService + Interactive user.
    /// Falls back to default security on non-Windows platforms (for build/test).
    /// </summary>
    private static NamedPipeServerStream CreateSecurePipe(string pipeName)
    {
        if (OperatingSystem.IsWindows())
        {
            var security = new PipeSecurity();
            security.AddAccessRule(new PipeAccessRule(
                new System.Security.Principal.SecurityIdentifier(
                    System.Security.Principal.WellKnownSidType.LocalSystemSid, null),
                PipeAccessRights.FullControl,
                System.Security.AccessControl.AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(
                new System.Security.Principal.SecurityIdentifier(
                    System.Security.Principal.WellKnownSidType.LocalServiceSid, null),
                PipeAccessRights.FullControl,
                System.Security.AccessControl.AccessControlType.Allow));
            // Helper runs as the interactive user — needs read/write to operate its own pipe.
            // Core (SYSTEM) connects in, so SYSTEM must also have access (granted above).
            security.AddAccessRule(new PipeAccessRule(
                new System.Security.Principal.SecurityIdentifier(
                    System.Security.Principal.WellKnownSidType.InteractiveSid, null),
                PipeAccessRights.ReadWrite,
                System.Security.AccessControl.AccessControlType.Allow));

            return NamedPipeServerStreamAcl.Create(
                pipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
                0, 0, security);
        }

        return new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    }

    /// <summary>
    /// Verifies the connecting client is SuavoAgent.Core and its executable lives under
    /// the SuavoAgent install root. Fail-closed on any read error.
    /// </summary>
    /// <summary>
    /// True if the connected pipe client's token carries a well-known SERVICE SID
    /// (LocalSystem / LocalService / NetworkService) — Core's identities. Read by
    /// impersonating the connected peer (identity-level; no SeImpersonatePrivilege needed),
    /// so it works across the user→SYSTEM boundary and is immune to PID-reuse races. An
    /// interactive-user impostor cannot present a service SID. Returns false (never throws)
    /// when the client is non-service or the read is inconclusive — caller then falls back
    /// to the image-path check.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private bool TryGetClientServiceSid(NamedPipeServerStream pipe, uint clientPid, out string sidLabel)
    {
        sidLabel = "";
        try
        {
            SecurityIdentifier? sid = null;
            pipe.RunAsClient(() =>
            {
                using var id = WindowsIdentity.GetCurrent();
                sid = id.User;
            });
            if (sid is null) return false;
            var isService =
                sid.IsWellKnown(WellKnownSidType.LocalSystemSid)
                || sid.IsWellKnown(WellKnownSidType.LocalServiceSid)
                || sid.IsWellKnown(WellKnownSidType.NetworkServiceSid);
            if (isService) { sidLabel = sid.Value; return true; }
            return false;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "IpcCommandServer: client SID check inconclusive for PID {Pid} — falling back to image-path check", clientPid);
            return false;
        }
    }

    private bool VerifyClientIsCore(NamedPipeServerStream pipe)
    {
        if (!OperatingSystem.IsWindows()) return true;

        try
        {
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var clientPid))
            {
                _logger.Warning("IpcCommandServer: GetNamedPipeClientProcessId failed — rejecting");
                return false;
            }

            var clientProc = System.Diagnostics.Process.GetProcessById((int)clientPid);
            if (clientProc.ProcessName != "SuavoAgent.Core")
            {
                _logger.Warning("IpcCommandServer: rejected connection from {Name} (PID {Pid}) — not SuavoAgent.Core",
                    clientProc.ProcessName, clientPid);
                return false;
            }

            // Primary cross-privilege identity proof: the client's TOKEN SID. Core runs as a Windows
            // service (SYSTEM/LocalService), so its token carries a well-known service SID that an
            // interactive-user impostor CANNOT forge (becoming a service requires admin). We read the
            // SID by impersonating the connected client over the pipe — this reads the REAL connected
            // peer's token (not a PID re-lookup, so it's free of the GetProcessById PID-reuse TOCTOU),
            // and works even when the de-privileged Helper can't OpenProcess/read SYSTEM Core's image
            // path (the original strand cause). Reading identity does not require SeImpersonatePrivilege.
            // Replaces the reverted ACCESS_DENIED-accept that Codex found exploitable.
            if (TryGetClientServiceSid(pipe, clientPid, out var serviceSidLabel))
            {
                _logger.Information(
                    "IpcCommandServer: accepting Core (PID {Pid}) — client token is service SID {Sid}; ProcessName=SuavoAgent.Core verified (unforgeable by an interactive user)",
                    clientPid, serviceSidLabel);
                return true;
            }

            // Not a service token (or impersonation inconclusive): fall through to the image-path check.
            // QueryFullProcessImageName works across user/SYSTEM security tokens;
            // MainModule fallback for non-Windows or quirks.
            var clientPath = ProcessImageInterop.Get(clientPid);
            if (string.IsNullOrEmpty(clientPath))
            {
                try
                {
                    clientPath = clientProc.MainModule?.FileName;
                }
                catch (Exception ex)
                {
                    // REVERTED (v3.65): an earlier fix accepted on ACCESS_DENIED + verified Core name to
                    // stop the de-privileged-Helper pipe flap. Codex security review found that exploitable
                    // — a same-user impostor named "SuavoAgent.Core" can tighten its OWN process DACL to
                    // force ACCESS_DENIED on purpose, then connect via the Interactive pipe ACE and be
                    // accepted. ACCESS_DENIED proves access denial, NOT SYSTEM identity. Restored to the
                    // safe reject behavior. The correct fix (verify the pipe CLIENT TOKEN SID is
                    // SYSTEM/LocalService via RunAsClient impersonation — unforgeable by an interactive
                    // user) is a tested follow-up, NOT shipped blind to a PHI box.
                    if (_relaxClientPathValidation)
                    {
                        _logger.Warning(ex, "IpcCommandServer: process image path unreadable for PID {Pid} (ProcessName=SuavoAgent.Core verified) — accepting due to RelaxIpcClientPathValidation=true", clientPid);
                        return true;
                    }
                    _logger.Warning(ex, "IpcCommandServer: process image path unreadable for PID {Pid} — rejecting", clientPid);
                    return false;
                }
            }

            if (string.IsNullOrEmpty(clientPath))
            {
                if (_relaxClientPathValidation)
                {
                    _logger.Warning("IpcCommandServer: empty client path for PID {Pid} (ProcessName=SuavoAgent.Core verified) — accepting due to RelaxIpcClientPathValidation=true", clientPid);
                    return true;
                }
                _logger.Warning("IpcCommandServer: empty client path for PID {Pid} — rejecting", clientPid);
                return false;
            }

            // Helper's AppContext.BaseDirectory is the install dir for this binary.
            // Core lives as a sibling subdir under the same install root.
            var installRoot = Path.GetDirectoryName(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))
                ?? AppContext.BaseDirectory;
            var installDir = installRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!clientPath.StartsWith(installDir, StringComparison.OrdinalIgnoreCase))
            {
                _logger.Warning("IpcCommandServer: rejected connection from outside install dir: {Path} (expected under {Dir})",
                    clientPath, installDir);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "IpcCommandServer: client verification error — rejecting");
            return false;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static extern bool GetNamedPipeClientProcessId(
        Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint clientProcessId);

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
