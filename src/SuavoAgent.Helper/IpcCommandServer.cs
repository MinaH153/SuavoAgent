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

    public static string? Get(uint processId) => Get(processId, out _);

    /// <summary>
    /// Same as <see cref="Get(uint)"/> but surfaces the Win32 error on failure so the
    /// caller can LOG why the cross-token read failed (field boxes strand on silent
    /// nulls here — the QA-C5 reject then looks causeless in the log).
    /// </summary>
    public static string? Get(uint processId, out int lastWin32Error)
    {
        lastWin32Error = 0;
        var hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (hProcess == IntPtr.Zero)
        {
            lastWin32Error = Marshal.GetLastWin32Error();
            return null;
        }
        try
        {
            var sb = new StringBuilder(1024);
            uint size = (uint)sb.Capacity;
            if (QueryFullProcessImageNameW(hProcess, 0, sb, ref size)) return sb.ToString();
            lastWin32Error = Marshal.GetLastWin32Error();
            return null;
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
///   - ACL restricts pipe to SYSTEM + the exact SuavoAgent.Core service SID
///   - Client token must carry that enabled per-service SID
///   - Client process name and exact executable path must identify SuavoAgent.Core
/// Without this, any local process running as the same user could drive UIA
/// automation of PioneerRx.
/// </summary>
public sealed partial class IpcCommandServer : IDisposable
{
    private readonly string _pipeName;
    private readonly PricingWorkflow _pricing;
    private readonly ScreenCaptureController? _vision;
    private readonly VisionGenerationGate _visionGenerationGate;
    private readonly VisionRuntimeStatusTracker? _visionRuntimeStatus;
    private readonly FileLocatorService? _locator;
    private readonly Func<bool>? _isPmsForeground;
    private readonly IntentCursorController? _intentCursor;
    private readonly SuavoAgent.Helper.Presence.PresencePreferenceStore? _presenceStore;
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

    internal Task Completion => _listenTask ?? Task.CompletedTask;

    private readonly bool _relaxClientPathValidation;

    // ------------------------------------------------------------------
    // Dispatch wedge watchdog. This server keeps one active pipe plus one pending listener but
    // deliberately has ONE sequential connection handler. Several dispatches run synchronous
    // UIA/COM work (pricing lookup, actuation) that can hang FOREVER against a hung PMS or a
    // torn-down session. A wedged
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
        VisionGenerationGate visionGenerationGate,
        ScreenCaptureController? vision = null,
        FileLocatorService? locator = null,
        Func<bool>? isPmsForeground = null,
        IntentCursorController? intentCursor = null,
        ActuationCommandHandler? actuation = null,
        PioneerRxCommandHandler? pioneerRx = null,
        SendInputDriver? sandboxDriver = null,
        bool relaxClientPathValidation = false,
        Action? onWedgedDispatch = null,
        SuavoAgent.Helper.Presence.PresencePreferenceStore? presenceStore = null,
        VisionRuntimeStatusTracker? visionRuntimeStatus = null)
    {
        _presenceStore = presenceStore;
        _pipeName = pipeName;
        _pricing = pricing;
        _visionGenerationGate = visionGenerationGate ??
            throw new ArgumentNullException(nameof(visionGenerationGate));
        _visionRuntimeStatus = visionRuntimeStatus;
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
        // QA C5: the relax flag no longer grants acceptance of an unverified peer. Surface it so an
        // operator who set it knows it's inert and the field stays referenced (no dead-flag smell).
        if (_relaxClientPathValidation)
        {
            _logger.Warning("IpcCommandServer: RelaxIpcClientPathValidation is set but is now INERT (QA C5) — "
                + "an unreadable/empty-path peer is never accepted; only the exact SuavoAgent.Core service SID plus binary identity passes. Remove the flag.");
        }
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _listenTask = ListenLoop(_cts.Token);
    }

    private async Task ListenLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pending = null;
            try
            {
                pending = CreateSecurePipe(_pipeName);
                Task pendingConnection = WaitForConnectionAsync(pending, ct);

                while (!ct.IsCancellationRequested)
                {
                    await pendingConnection.ConfigureAwait(false);

                    // Promote the connected listener, then establish and begin accepting on its
                    // successor BEFORE handling any command. A second Core connection can now wait
                    // on a real server instance instead of the retiring listener's OS backlog. We
                    // still dispatch only one connection at a time, preserving UIA/COM serialization.
                    var active = pending ?? throw new InvalidOperationException(
                        "Command pipe listener promotion invariant failed.");
                    pending = null;
                    try
                    {
                        pending = CreateSecurePipe(_pipeName);
                        pendingConnection = WaitForConnectionAsync(pending, ct);

                        // Client verification happens INSIDE HandleConnection, after the first frame
                        // is read and before that frame is dispatched. VerifyClientIsCore's primary
                        // identity proof (token-SID via ImpersonateNamedPipeClient) only works once
                        // the server has read a message from the pipe. Reading one bounded frame from
                        // an as-yet-unverified peer is safe: the ACL already restricts connectors, and
                        // NO command is dispatched until verification passes.
                        await HandleConnection(active, ct).ConfigureAwait(false);
                    }
                    finally
                    {
                        active.Dispose();
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Warning(
                    "IpcCommandServer: connection error ({ExceptionType})",
                    ex.GetType().Name);
                await Task.Delay(1000, ct);
            }
            finally
            {
                pending?.Dispose();
            }
        }
    }

    private async Task WaitForConnectionAsync(
        NamedPipeServerStream pipe,
        CancellationToken ct)
    {
        _logger.Debug("IpcCommandServer: waiting for Core on pipe {Name}", _pipeName);
        await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
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
                    // The generation proof is connection-bound. A prior Core
                    // connection must never leave vision authorized for a new
                    // authenticated peer that omits or fails its handshake.
                    _visionGenerationGate.Reset();
                    _logger.Information("IpcCommandServer: Core connected + verified on pipe {Name}", _pipeName);
                }

                var request = JsonSerializer.Deserialize<IpcRequest>(json);
                if (request == null) continue;

                _logger.Debug("IpcCommandServer: received {Command} [{Id}]", request.Command, request.Id);

                var response = await DispatchGuardedAsync(request, ct);
                var responseJson = JsonSerializer.Serialize(response);
                // QA I1: a response over the frame limit makes WriteFrameAsync throw, which the catch below
                // swallows — leaving NO response and stranding the caller for its full timeout (e.g. a large
                // find_file/discover_elements result, 30-60s hang). Fail fast with a small, always-fitting
                // error frame so the caller errors immediately. Byte count mirrors IpcFraming's own check.
                if (System.Text.Encoding.UTF8.GetByteCount(responseJson) > IpcFraming.MaxPayloadSize)
                {
                    _logger.Warning("IpcCommandServer: {Command} [{Id}] response over the {Max}-byte frame limit — returning response_too_large",
                        request.Command, request.Id, IpcFraming.MaxPayloadSize);
                    responseJson = JsonSerializer.Serialize(
                        Error(request.Id, request.Command, "response_too_large",
                            $"Response exceeded the {IpcFraming.MaxPayloadSize}-byte IPC frame limit; narrow the request (e.g. fewer candidates)."));
                }
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
                _logger.Warning(
                    "IpcCommandServer: message error ({ExceptionType})",
                    ex.GetType().Name);
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

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
