using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Serilog;
using SuavoAgent.Contracts.Discovery;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Core.Discovery;
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
    private readonly ILogger _logger;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;

    public IpcCommandServer(
        string pipeName,
        PricingWorkflow pricing,
        ILogger logger,
        ScreenCaptureController? vision = null,
        FileLocatorService? locator = null)
    {
        _pipeName = pipeName;
        _pricing = pricing;
        _vision = vision;
        _locator = locator;
        _logger = logger;
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

                if (!VerifyClientIsCore(pipe))
                {
                    pipe.Disconnect();
                    continue;
                }

                _logger.Information("IpcCommandServer: Core connected on pipe {Name}", _pipeName);

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
        while (!ct.IsCancellationRequested && pipe.IsConnected)
        {
            try
            {
                var json = await IpcFraming.ReadFrameAsync(pipe, ct);
                if (json == null) break;

                var request = JsonSerializer.Deserialize<IpcRequest>(json);
                if (request == null) continue;

                _logger.Debug("IpcCommandServer: received {Command} [{Id}]", request.Command, request.Id);

                var response = await DispatchAsync(request, ct);
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

    private Task<IpcResponse> DispatchAsync(IpcRequest request, CancellationToken ct)
    {
        return request.Command switch
        {
            IpcCommands.PricingLookup => HandlePricingLookupAsync(request, ct),
            IpcCommands.CaptureScreen => HandleCaptureScreenAsync(request, ct),
            IpcCommands.FindFile => HandleFindFileAsync(request, ct),
            IpcCommands.Ping => Task.FromResult(Ok(request.Id, request.Command, null)),
            _ => Task.FromResult(Error(request.Id, request.Command, "unknown_command", $"Unknown command: {request.Command}"))
        };
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
        if (_vision == null)
        {
            return Error(request.Id, request.Command, "vision_unavailable",
                "Vision not configured in this Helper instance");
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

    private static IpcResponse Error(string id, string command, string code, string message) =>
        new(id, IpcStatus.InternalError, command, null,
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
                    _logger.Warning(ex, "IpcCommandServer: process image path unreadable for PID {Pid} — rejecting", clientPid);
                    return false;
                }
            }

            if (string.IsNullOrEmpty(clientPath))
            {
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
