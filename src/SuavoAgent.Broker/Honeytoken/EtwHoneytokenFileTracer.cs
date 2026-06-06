using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Contracts.Models;

namespace SuavoAgent.Broker.Honeytoken;

/// <summary>
/// The honeytoken attribution ORACLE (LocalSystem). A private real-time Microsoft-Windows-Kernel-File ETW
/// session, hard-filtered to the decoy dir, that names WHO opened the decoy (the kernel Create event's
/// ProcessID is the TRUE IRP issuer — not a held-handle guess) and hands it to the Helper via the signed,
/// ACL-locked <see cref="HoneytokenAttributionStore"/>. The Helper's EtwBrokerFileAccessAttributor consumes it.
///
/// Carries every mitigation from the adversarial design review:
///   • PHI WALL — the hot callback hard-filters to the decoy dir's NT prefix BEFORE any allocation/resolve, so
///     no other (PHI) path ever leaves the callback; the raw event path is NEVER logged; the handoff stores
///     only the constant decoy TokenId (no raw-path field exists).
///   • EMPTY-PREFIX REFUSE — a null/empty NT prefix would invert the filter to match EVERY open; the self-test
///     refuses to arm in that case (LogError + run blind, Broker online).
///   • FORGERY DEFENSE — after the atomic write, the per-file ACL strips inherited INTERACTIVE-write and grants
///     the user READ-only; if the ACL can't be applied the file is DELETED (fail-closed) — the user must not be
///     able to forge a {ProcessName:'powershell'} handoff and self-brick.
///   • NAME FORMAT — ProcessName is stored as the bare filename-without-extension the corroborator denylist
///     matches (defense-in-depth; the Helper attributor also normalizes).
///   • CLEAN LIFECYCLE — leaked same-named session reclaimed on start (survives Broker Exit(1)/OTA); session
///     stopped on token-cancel + Dispose so Process() unblocks; EventsLost logged. Fail-OPEN throughout: any
///     ETW error leaves the oracle off and the Broker online.
///
/// Inert until BOTH the Helper's HoneytokenAttributorMode=EtwBroker AND HoneytokenReflexEnabled are flipped
/// (default off → the doc is just written and ignored). Arming is gated on a per-pharmacy shadow day.
/// </summary>
public sealed class EtwHoneytokenFileTracer : BackgroundService
{
    internal const string SessionName = "SuavoAgent-HoneytokenFileTrace";
    private static readonly Guid KernelFileProvider = new("edd08927-9cc4-4e65-b970-c2560fb5c289");
    private const ulong KeywordCreate = 0x80;   // CREATE — handle-open of an existing file (the decoy)
    private const ulong KeywordFileName = 0x10; // FILENAME — carries the path inline
    private const ulong Keywords = KeywordCreate | KeywordFileName; // 0x90

    private readonly ILogger<EtwHoneytokenFileTracer> _logger;
    private readonly string _win32DecoyDir;
    private readonly string _handoffPath;
    private readonly string _decoyTokenId;
    private readonly string _pipeNonce;
    private readonly string _hardenedTempDir;

    private DevicePathNormalizer? _normalizer;
    private volatile string _ntDecoyPrefix = string.Empty;
    private TraceEventSession? _session;
    private Thread? _processThread;

    public EtwHoneytokenFileTracer(ILogger<EtwHoneytokenFileTracer> logger)
    {
        _logger = logger;
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        _win32DecoyDir = Path.Combine(programData, "SuavoAgent", HoneytokenConstants.DirName);
        _handoffPath = HoneytokenAttributionStore.GetDefaultPath();
        _decoyTokenId = HoneytokenConstants.ComputeId();
        _pipeNonce = ReadPipeNonce() ?? string.Empty;
        _hardenedTempDir = Path.Combine(Path.GetDirectoryName(_handoffPath)!, ".hnytmp");
    }

    /// <summary>Test-only ctor: inject isolated paths so the live windows smoke runs against a temp decoy dir.</summary>
    internal EtwHoneytokenFileTracer(
        ILogger<EtwHoneytokenFileTracer> logger, string decoyDir, string handoffPath, string pipeNonce)
    {
        _logger = logger;
        _win32DecoyDir = decoyDir;
        _handoffPath = handoffPath;
        _decoyTokenId = HoneytokenConstants.ComputeId();
        _pipeNonce = pipeNonce;
        _hardenedTempDir = Path.Combine(Path.GetDirectoryName(_handoffPath)!, ".hnytmp");
    }

    /// <summary>Test seam: arm the live ETW session synchronously (windows smoke only).</summary>
    [SupportedOSPlatform("windows")]
    internal void ArmForTest() => ArmWindows(CancellationToken.None);

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!OperatingSystem.IsWindows())
            return Task.CompletedTask; // managed lib builds cross-platform; ETW is Windows-only

        try
        {
            ArmWindows(stoppingToken);
        }
        catch (Exception ex)
        {
            // FAIL-OPEN: a missing native dll / non-elevated / any ETW error → oracle off, Broker online.
            _logger.LogError(ex, "ETW honeytoken oracle arm failed — running blind, Broker online");
        }
        return Task.CompletedTask;
    }

    [SupportedOSPlatform("windows")]
    private void ArmWindows(CancellationToken stoppingToken)
    {
        _normalizer = new DevicePathNormalizer();
        _normalizer.Refresh(); // build the NT-device map from the live OS before the self-test
        if (!SelfTestOrLogBlind())
            return; // refused to arm (e.g. empty NT prefix) — logged at ERROR, run blind

        EnsureHardenedTempDir(); // SYSTEM-only scratch dir so handoff temps are born non-user-writable

        // Reclaim a same-named session orphaned by a prior Broker crash/OTA (a real-time ETW session is a
        // named kernel object that outlives the process).
        try { TraceEventSession.GetActiveSession(SessionName)?.Stop(); }
        catch (Exception ex) { _logger.LogWarning(ex, "stale ETW session stop (non-fatal)"); }

        _session = new TraceEventSession(SessionName) { StopOnDispose = true };
        _session.EnableProvider(KernelFileProvider, TraceEventLevel.Informational, Keywords);
        _session.Source.Dynamic.All += OnFileEvent;

        // Source.Process() BLOCKS forever until Stop(); run it on its own background thread. Stopping the
        // session (on cancel or Dispose) unblocks it. IsBackground so it never blocks process exit.
        _processThread = new Thread(() =>
        {
            try { _session.Source.Process(); }
            catch (Exception ex) { _logger.LogWarning(ex, "ETW honeytoken process loop ended"); }
            finally
            {
                var lost = _session?.Source?.EventsLost ?? 0;
                if (lost > 0) _logger.LogWarning("ETW honeytoken session EventsLost={Lost}", lost);
            }
        })
        { IsBackground = true, Name = SessionName };
        _processThread.Start();

        stoppingToken.Register(() => { try { _session?.Stop(); } catch { /* best-effort */ } });
        _logger.LogInformation("ETW honeytoken oracle ARMED: prefix-resolved, handoff={Path}", _handoffPath);
    }

    [SupportedOSPlatform("windows")]
    private void OnFileEvent(TraceEvent data)
    {
        try
        {
            if (!string.Equals(data.EventName, "Create", StringComparison.Ordinal)) return;
            var ntPath = data.PayloadByName("FileName") as string;

            // PHI WALL + empty-prefix guard: reject everything not under the decoy dir BEFORE any work. A
            // non-decoy (PHI) path never leaves this stack frame; an empty prefix matches NOTHING (never all).
            if (!MatchesDecoy(ntPath, _ntDecoyPrefix)) return;

            // Resolve the TRUE IRP issuer, validated against PID reuse (a recycled PID's process started after
            // this event → rejected → null → unknown toucher → Degrade, never apoptosis on an innocent process).
            var eventUtc = new DateTimeOffset(data.TimeStamp.ToUniversalTime());
            var exePath = ProcessImageInterop.Get((uint)data.ProcessID, eventUtc);
            var name = DeriveProcessName(exePath);

            var entry = new HoneytokenAttributionEntry(
                TokenId: _decoyTokenId,               // CLAMP to the constant id — never the observed path
                ProcessId: data.ProcessID,
                ProcessName: name,
                ExePath: exePath,
                ObservedAt: DateTimeOffset.UtcNow);

            // Harden the TEMP file BEFORE publish so the destination never exists user-writable; if hardening
            // throws, Write deletes the temp and publishes NOTHING (fail-closed — no forgeable handoff).
            HoneytokenAttributionStore.Write(
                _handoffPath, _pipeNonce, new[] { entry }, DateTimeOffset.UtcNow, HardenHandoffTmp, _hardenedTempDir);
            // NOTE: deliberately NO log of ntPath/exePath here — names can be benign-but-sensitive and the
            // log sink is not PHI-guarded. The handoff doc + heartbeat carry the (clamped) signal.
        }
        catch
        {
            // Swallow per-event errors (never the exception object — it could embed the path). Oracle stays up.
        }
    }

    /// <summary>
    /// Resolve the decoy NT prefix and prove the production filter fires on it. Returns false (run blind, LOUD)
    /// if the prefix can't be resolved — never arm with an empty prefix (which would match every open).
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal bool SelfTestOrLogBlind()
    {
        var prefix = _normalizer?.ToNtPrefix(_win32DecoyDir);
        if (!SelfTestPrefix(prefix, HoneytokenConstants.TokenFileName, out var reason))
        {
            _logger.LogError("ETW honeytoken oracle BLIND: {Reason} (dir={Dir}) — attribution disabled", reason, _win32DecoyDir);
            _ntDecoyPrefix = string.Empty;
            return false;
        }
        _ntDecoyPrefix = prefix!;
        return true;
    }

    /// <summary>
    /// Lock down the TEMP handoff before it is published (called by Store.Write pre-move). Strips inheritance,
    /// PURGES every existing explicit ACE (so no pre-seeded rule survives), then grants ONLY LocalSystem +
    /// Administrators write and the interactive user READ-only — the pharmacy user can verify but cannot forge
    /// a {ProcessName:'powershell'} handoff and self-brick. THROWS on failure (no try/catch) so Store.Write
    /// deletes the temp and publishes nothing — there is never an un-ACL'd file at the destination.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private void HardenHandoffTmp(string tmpPath)
    {
        var fi = new FileInfo(tmpPath);
        var sec = fi.GetAccessControl();
        sec.SetAccessRuleProtection(true, false); // strip inherited ACEs (incl. the dir's INTERACTIVE Modify)
        foreach (FileSystemAccessRule rule in sec.GetAccessRules(true, false, typeof(SecurityIdentifier)))
            sec.RemoveAccessRuleSpecific(rule); // purge any explicit ACEs → only our three remain
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
        {
            sec.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(sid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        }
        sec.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.InteractiveSid, null),
            FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        fi.SetAccessControl(sec);
    }

    /// <summary>
    /// Create + lock the SYSTEM-only scratch dir where handoff temps are written, so a temp inherits a
    /// non-user-writable ACL AT CREATION (closing the pre-harden inherited-ACL window). Protected (no
    /// inheritance from ProgramData\SuavoAgent, which grants INTERACTIVE Modify), LocalSystem + Admins
    /// FullControl with Container|Object inherit → children are born SYSTEM-only. Best-effort: on failure the
    /// dir may fall back to inherited ACLs, but the per-file harden + fail-closed publish still gate forgery.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private void EnsureHardenedTempDir()
    {
        try
        {
            var di = Directory.CreateDirectory(_hardenedTempDir);
            var sec = di.GetAccessControl();
            sec.SetAccessRuleProtection(true, false); // strip ProgramData's inherited INTERACTIVE Modify
            foreach (FileSystemAccessRule rule in sec.GetAccessRules(true, false, typeof(SecurityIdentifier)))
                sec.RemoveAccessRuleSpecific(rule);
            foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            {
                sec.AddAccessRule(new FileSystemAccessRule(
                    new SecurityIdentifier(sid, null),
                    FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow));
            }
            di.SetAccessControl(sec);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ETW honeytoken temp dir lockdown failed (non-fatal; per-file harden still gates)");
        }
    }

    public override void Dispose()
    {
        try { _session?.Stop(); } catch { /* unblocks Process() */ }
        try { _session?.Dispose(); } catch { /* best-effort */ }
        base.Dispose();
    }

    // --- Pure, headless-testable helpers ---------------------------------------

    /// <summary>True only if <paramref name="ntPath"/> is a file DIRECTLY under the decoy dir. Empty-prefix
    /// safe (empty prefix → false for everything); the trailing-separator guard kills a "honeytokens-evil"
    /// sibling-dir false match.</summary>
    internal static bool MatchesDecoy(string? ntPath, string? ntDecoyPrefix)
    {
        if (string.IsNullOrEmpty(ntPath) || string.IsNullOrEmpty(ntDecoyPrefix)) return false;
        if (ntPath.Length <= ntDecoyPrefix.Length) return false;
        if (!ntPath.StartsWith(ntDecoyPrefix, StringComparison.OrdinalIgnoreCase)) return false;
        return ntPath[ntDecoyPrefix.Length] == '\\';
    }

    /// <summary>Reduce a full exe path / "x.exe" / bare name to the lowercase filename-without-extension the
    /// corroborator's name-only denylist compares against. Splits on '\' AND '/' so it is correct regardless
    /// of host OS. Null/blank → null (→ unknown toucher → Degrade).</summary>
    internal static string? DeriveProcessName(string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return null;
        var trimmed = exePath.Trim();
        var lastSep = trimmed.LastIndexOfAny(new[] { '\\', '/' });
        var fileName = lastSep >= 0 ? trimmed[(lastSep + 1)..] : trimmed;
        var dot = fileName.LastIndexOf('.');
        var name = dot > 0 ? fileName[..dot] : fileName;
        return name.Length == 0 ? null : name.ToLowerInvariant();
    }

    /// <summary>Pure self-test core: prefix must be non-empty AND the synth decoy NT path must pass the
    /// production filter. <paramref name="reason"/> is a PHI-safe diagnostic.</summary>
    internal static bool SelfTestPrefix(string? ntDecoyPrefix, string tokenFileName, out string reason)
    {
        if (string.IsNullOrWhiteSpace(ntDecoyPrefix))
        {
            reason = "decoy dir did not resolve to an NT device path";
            return false;
        }
        var synth = ntDecoyPrefix + "\\" + tokenFileName;
        if (!MatchesDecoy(synth, ntDecoyPrefix))
        {
            reason = "synthesized decoy NT path failed the production filter";
            return false;
        }
        reason = "ok";
        return true;
    }

    private static string? ReadPipeNonce()
    {
        try
        {
            var noncePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "SuavoAgent", "pipe.nonce");
            return File.Exists(noncePath) ? File.ReadAllText(noncePath).Trim() : null;
        }
        catch { return null; }
    }
}
