using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Serilog;
using SuavoAgent.Contracts.Security;
using SuavoAgent.Contracts.Vision;
using SuavoAgent.Core.Config;
using SuavoAgent.Helper.Actuation;

namespace SuavoAgent.Helper.Vision;

internal readonly record struct ApprovedPmsWindowTarget(
    IntPtr Hwnd,
    int ProcessId,
    string TrustBinding);

internal readonly record struct PmsProcessTrustEvidence(
    bool Trusted,
    string TrustBinding,
    string Code)
{
    public static PmsProcessTrustEvidence Denied(string code) => new(false, string.Empty, code);
}

internal interface IApprovedPmsWindowAuthority
{
    bool IsSupported { get; }

    bool TryResolveForeground(out ApprovedPmsWindowTarget target, out string code);

    bool Revalidate(ApprovedPmsWindowTarget target, out string code);
}

/// <summary>
/// Resolves and revalidates one exact foreground HWND owned by the exact PioneerRx
/// process identity in the current maintenance-key/cloud-co-signed approval.
/// Window titles and executable paths are never returned or logged.
/// </summary>
internal sealed class WindowsApprovedPmsWindowAuthority : IApprovedPmsWindowAuthority
{
    private readonly Func<IntPtr> _getForegroundWindow;
    private readonly Func<IntPtr, bool> _isWindowVisible;
    private readonly Func<IntPtr, int> _getWindowProcessId;
    private readonly Func<int, PmsProcessTrustEvidence> _verifyProcess;

    public WindowsApprovedPmsWindowAuthority()
        : this(
            OperatingSystem.IsWindows(),
            GetForegroundWindow,
            IsWindowVisible,
            GetOwningProcessId,
            VerifySignedPioneerRxProcess)
    {
    }

    internal WindowsApprovedPmsWindowAuthority(
        bool isSupported,
        Func<IntPtr> getForegroundWindow,
        Func<IntPtr, bool> isWindowVisible,
        Func<IntPtr, int> getWindowProcessId,
        Func<int, PmsProcessTrustEvidence> verifyProcess)
    {
        IsSupported = isSupported;
        _getForegroundWindow = getForegroundWindow;
        _isWindowVisible = isWindowVisible;
        _getWindowProcessId = getWindowProcessId;
        _verifyProcess = verifyProcess;
    }

    public bool IsSupported { get; }

    public bool TryResolveForeground(out ApprovedPmsWindowTarget target, out string code)
    {
        target = default;
        code = "pms_window_unavailable";
        if (!IsSupported) return false;

        try
        {
            return TryResolveForegroundCore(out target, out code);
        }
        catch
        {
            target = default;
            code = "pms_window_probe_failed";
            return false;
        }
    }

    private bool TryResolveForegroundCore(out ApprovedPmsWindowTarget target, out string code)
    {
        target = default;
        code = "pms_window_unavailable";

        var hwnd = _getForegroundWindow();
        if (hwnd == IntPtr.Zero)
        {
            code = "foreground_window_missing";
            return false;
        }

        if (!_isWindowVisible(hwnd))
        {
            code = "foreground_window_not_visible";
            return false;
        }

        var pid = _getWindowProcessId(hwnd);
        if (pid <= 0)
        {
            code = "foreground_process_unavailable";
            return false;
        }

        var trust = _verifyProcess(pid);
        if (!trust.Trusted || string.IsNullOrWhiteSpace(trust.TrustBinding))
        {
            code = SafeCode(trust.Code);
            return false;
        }

        target = new ApprovedPmsWindowTarget(hwnd, pid, trust.TrustBinding);
        code = "approved";
        return true;
    }

    public bool Revalidate(ApprovedPmsWindowTarget target, out string code)
    {
        code = "pms_window_changed";
        if (!IsSupported || target.Hwnd == IntPtr.Zero || target.ProcessId <= 0)
            return false;

        try
        {
            return RevalidateCore(target, out code);
        }
        catch
        {
            code = "pms_window_probe_failed";
            return false;
        }
    }

    private bool RevalidateCore(ApprovedPmsWindowTarget target, out string code)
    {
        code = "pms_window_changed";

        // Exact HWND equality is intentional. Another window from the same PMS
        // process is not the surface the operator authorized at capture start.
        if (_getForegroundWindow() != target.Hwnd)
        {
            code = "foreground_window_changed";
            return false;
        }

        if (!_isWindowVisible(target.Hwnd))
        {
            code = "foreground_window_not_visible";
            return false;
        }

        if (_getWindowProcessId(target.Hwnd) != target.ProcessId)
        {
            code = "foreground_process_changed";
            return false;
        }

        var trust = _verifyProcess(target.ProcessId);
        if (!trust.Trusted || string.IsNullOrWhiteSpace(trust.TrustBinding))
        {
            code = SafeCode(trust.Code);
            return false;
        }

        if (!FixedTimeEquals(target.TrustBinding, trust.TrustBinding))
        {
            code = "pms_process_trust_changed";
            return false;
        }

        code = "approved";
        return true;
    }

    private static PmsProcessTrustEvidence VerifySignedPioneerRxProcess(int pid)
    {
        try
        {
            // Reload the SYSTEM-published approval generation for every check.
            // Revocation or replacement therefore invalidates an in-flight capture.
            var approval = PioneerRxProcessApprovalLoader.Load(verifyExecutable: false);
            if (!approval.Approved || approval.Receipt is null)
                return PmsProcessTrustEvidence.Denied(SafeCode(approval.Code));

            var verdict = new PioneerRxProcessTrustVerifier(approval).VerifyResolvedProcess(pid);
            if (!verdict.Trusted)
                return PmsProcessTrustEvidence.Denied(SafeCode(verdict.Code));

            return new PmsProcessTrustEvidence(
                true,
                ComputeTrustBinding(approval.Receipt),
                "trusted");
        }
        catch
        {
            return PmsProcessTrustEvidence.Denied("pms_process_trust_unavailable");
        }
    }

    internal static string ComputeTrustBinding(PioneerRxProcessApprovalReceipt receipt)
    {
        // Bind the exact signed receipt envelope, not a mutable process name.
        // The value is retained in memory only and never written to logs.
        var canonicalEnvelope = string.Join('|',
            PioneerRxProcessApprovalContract.Canonical(receipt),
            receipt.MaintenancePublicKeySpki,
            receipt.MaintenanceSignature,
            receipt.CloudCoApprovalSignature);
        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(canonicalEnvelope)))
            .ToLowerInvariant();
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length &&
               CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    internal static string SafeCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > 64)
            return "pms_capture_authorization_failed";
        foreach (var ch in code)
        {
            if (ch is not (>= 'a' and <= 'z') and not (>= '0' and <= '9') and not '_')
                return "pms_capture_authorization_failed";
        }
        return code;
    }

    private static int GetOwningProcessId(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        return pid is 0 or > int.MaxValue ? 0 : (int)pid;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}

/// <summary>
/// Live-PMS capture boundary. Each request dynamically resolves the exact
/// foreground window, proves its PID is the signed PioneerRx executable, and
/// then captures only that HWND with <c>PrintWindow</c>. The same HWND, PID,
/// and signed trust binding are checked immediately before and after capture.
/// Full-desktop/framebuffer capture is intentionally unavailable here.
/// </summary>
public sealed class ApprovedPmsForegroundWindowCapture : IScreenCapture
{
    internal delegate IScreenCapture WindowCaptureFactory(
        ApprovedPmsWindowTarget target,
        Func<IntPtr, int, bool> authorizeImmediatelyBeforePrintWindow);

    private readonly VisionOptions _options;
    private readonly ILogger _logger;
    private readonly IApprovedPmsWindowAuthority _authority;
    private readonly WindowCaptureFactory _captureFactory;
    private readonly IntPtr _requiredHwnd;
    private readonly int _requiredPid;
    private readonly object _rateLock = new();
    private long _lastCaptureTicks = -1;

    public ApprovedPmsForegroundWindowCapture(
        IOptions<AgentOptions> options,
        ILogger logger)
        : this(
            options,
            logger,
            new WindowsApprovedPmsWindowAuthority(),
            (target, authorize) => BuildPlatformWindowCapture(
                options, logger, target, authorize),
            IntPtr.Zero,
            0)
    {
    }

    internal ApprovedPmsForegroundWindowCapture(
        IOptions<AgentOptions> options,
        ILogger logger,
        IApprovedPmsWindowAuthority authority,
        WindowCaptureFactory captureFactory,
        IntPtr requiredHwnd = default,
        int requiredPid = 0)
    {
        _options = options.Value.Vision;
        _logger = logger;
        _authority = authority;
        _captureFactory = captureFactory;
        _requiredHwnd = requiredHwnd;
        _requiredPid = requiredPid;
    }

    public bool IsAvailable => _options.Enabled && _authority.IsSupported;

    internal static ApprovedPmsForegroundWindowCapture ForExpectedForegroundWindow(
        IOptions<AgentOptions> options,
        ILogger logger,
        IntPtr requiredHwnd,
        int requiredPid) => new(
            options,
            logger,
            new WindowsApprovedPmsWindowAuthority(),
            (target, authorize) => BuildPlatformWindowCapture(
                options, logger, target, authorize),
            requiredHwnd,
            requiredPid);

    private static IScreenCapture BuildPlatformWindowCapture(
        IOptions<AgentOptions> options,
        ILogger logger,
        ApprovedPmsWindowTarget target,
        Func<IntPtr, int, bool> authorize)
    {
        if (!OperatingSystem.IsWindows()) return new NullScreenCapture();
        return new WindowScopedScreenCapture(
            options,
            logger,
            target.Hwnd,
            target.ProcessId,
            authorize);
    }

    public async Task<ScreenBytes?> CapturePrimaryAsync(CancellationToken ct)
    {
        if (!IsAvailable || !RateLimiterAllows()) return null;

        try
        {
            if (!_authority.TryResolveForeground(out var target, out var code))
            {
                LogRefusal(code);
                return null;
            }
            if ((_requiredHwnd != IntPtr.Zero && target.Hwnd != _requiredHwnd) ||
                (_requiredPid > 0 && target.ProcessId != _requiredPid))
            {
                LogRefusal("requested_pms_window_changed");
                return null;
            }

            var capture = _captureFactory(
                target,
                (hwnd, pid) =>
                {
                    if (hwnd != target.Hwnd || pid != target.ProcessId)
                        return false;
                    var authorized = _authority.Revalidate(target, out var preCaptureCode);
                    if (!authorized) LogRefusal(preCaptureCode);
                    return authorized;
                });

            var screen = await capture.CapturePrimaryAsync(ct).ConfigureAwait(false);
            if (screen is null || screen.Value.Hwnd != target.Hwnd.ToInt64())
                return null;

            // Post-capture validation ensures pixels are discarded if the user
            // alt-tabs, the HWND is reused, or signed process authority changes
            // while PrintWindow is executing.
            if (!_authority.Revalidate(target, out var postCaptureCode))
            {
                LogRefusal(postCaptureCode);
                return null;
            }

            return screen;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            // Exception messages/objects can contain paths or captured window
            // text. Emit only the runtime type, which carries no PHI payload.
            _logger.Warning(
                "Approved PMS window capture failed ({ErrorType})",
                ex.GetType().Name);
            return null;
        }
    }

    private void LogRefusal(string code) => _logger.Information(
        "Approved PMS window capture refused ({Code})",
        WindowsApprovedPmsWindowAuthority.SafeCode(code));

    private bool RateLimiterAllows()
    {
        lock (_rateLock)
        {
            var now = Environment.TickCount64;
            var minInterval = Math.Max(0, _options.MinIntervalMs);
            if (_lastCaptureTicks >= 0 && now - _lastCaptureTicks < minInterval)
                return false;
            _lastCaptureTicks = now;
            return true;
        }
    }
}
