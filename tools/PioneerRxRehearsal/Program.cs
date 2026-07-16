using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PioneerRxSim;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Contracts.Discovery;
using SuavoAgent.Core.Discovery;
using SuavoAgent.Core.ActionGrammarV1.Verbs.Actuation;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Ipc;
using SuavoAgent.Core.Pricing;
using SuavoAgent.Core.State;
using SuavoAgent.Core.Vision;
using SuavoAgent.Core.Verticals.Pharmacy;

namespace PioneerRxRehearsal;

/// <summary>
/// Dress-rehearsal driver. Mirrors HeartbeatWorker.HandleFindAndRunPricingJobAsync's
/// decision sequence (that method is private on a cloud-coupled worker, so the glue is
/// reproduced here verbatim — preflight → discovery → path-safety → spec → executor),
/// while every load-bearing component is the REAL production class:
///   HelperInteractivePreflight, DiscoveryClient (find_file over IPC), IpcCommandClient,
///   AgentStateDb, ExcelPricingReader/Writer, PricingJobRunner, UiaFirstPricingJobExecutor,
///   and on the other side of the pipe the REAL SuavoAgent.Helper.exe (IpcCommandServer +
///   PricingWorkflow + PioneerRxUiaEngine) attached to the PioneerRxSim "PioneerPharmacy" app.
///
/// PricingExecutor=UiaFirst is selected by construction — the same composition Core's DI
/// makes when AgentOptions.PricingExecutor == PricingExecutorMode.UiaFirst.
/// </summary>
public static class Program
{
    private static readonly string[] PmsMarkerPaths =
    {
        // Mirror of Helper-internal PioneerRxInstallDetector.KnownPaths (driver-side fail-fast
        // only — without one of these, the REAL Helper parks before its attach loop and every
        // lookup would die with "PioneerRx main window not available").
        @"C:\Program Files (x86)\New Tech Computer Systems\PioneerRx\PioneerPharmacy.exe",
        @"C:\Program Files\New Tech Computer Systems\PioneerRx\PioneerPharmacy.exe",
        @"D:\Program Files (x86)\New Tech Computer Systems\PioneerRx\PioneerPharmacy.exe",
        @"D:\Program Files\New Tech Computer Systems\PioneerRx\PioneerPharmacy.exe",
    };

    public static async Task<int> Main(string[] args)
    {
        var a = Args.Parse(args);
        if (a is null) return 64;

        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* non-console host */ }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        var ct = cts.Token;

        using var loggerFactory = LoggerFactory.Create(b => b
            .SetMinimumLevel(LogLevel.Information)
            .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; }));
        var log = loggerFactory.CreateLogger("Rehearsal");

        Banner(a);

        // ── Self-checks ──────────────────────────────────────────────────────
        var processPath = Environment.ProcessPath ?? "";
        if (!Path.GetFileNameWithoutExtension(processPath).Equals("SuavoAgent.Core", StringComparison.OrdinalIgnoreCase))
        {
            log.LogWarning(
                "This process is '{Name}', not 'SuavoAgent.Core'. The Helper's IpcCommandServer.VerifyClientIsCore " +
                "gate (exercised REAL, never relaxed) will reject the command-pipe connection and the pre-flight will fail. " +
                "Launch via the staged, renamed apphost (rehearsal.ps1 sets this up).",
                Path.GetFileName(processPath));
        }

        if (OperatingSystem.IsWindows() && !a.IgnorePmsMarker && !PmsMarkerPaths.Any(File.Exists))
        {
            log.LogError(
                "No PioneerRx install marker found. The REAL Helper's PioneerRxInstallDetector checks for " +
                "PioneerPharmacy.exe under 'New Tech Computer Systems\\PioneerRx' and PARKS (never attaches) without it. " +
                "Run rehearsal.ps1 -CreatePmsMarker (admin) or create an empty file at: {Path} " +
                "(--ignore-pms-marker to proceed anyway).",
                PmsMarkerPaths[0]);
            return 3;
        }

        // ── Workbook ─────────────────────────────────────────────────────────
        var workDir = Path.Combine(a.StageDir, "work");
        Directory.CreateDirectory(workDir);
        string workbookPath;
        if (a.WorkbookPath is not null)
        {
            workbookPath = Path.GetFullPath(a.WorkbookPath);
        }
        else if (a.Mode == RunMode.Full)
        {
            // Full mode exercises the REAL file-discovery chain: Helper's FileLocatorService
            // scans the interactive user's Desktop/Downloads/Documents top-level.
            workbookPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                "Top 250 Generics NDC Pricing.xlsx");
            RehearsalWorkbook.WriteFull(workbookPath);
            log.LogInformation("Full-mode workbook written to Desktop for real discovery: {Path}", workbookPath);
        }
        else
        {
            workbookPath = Path.Combine(workDir, "rehearsal-pricing-quick.xlsx");
            RehearsalWorkbook.WriteQuick(workbookPath);
            log.LogInformation("Quick-mode workbook generated: {Path}", workbookPath);
        }

        // ── Launch sim + REAL Helper ─────────────────────────────────────────
        // Production pipe-name shape (Core writes SuavoAgent-{nonce} / SuavoAgent-cmd-{nonce}).
        var nonce = Guid.NewGuid().ToString("N")[..12];
        var mainPipe = $"SuavoAgent-{nonce}";
        var cmdPipe = a.CmdPipeOverride ?? $"SuavoAgent-cmd-{nonce}";

        Process? simProc = null, helperProc = null;
        try
        {
            if (!a.NoLaunch)
            {
                var simExe = Path.Combine(a.StageDir, "Sim", "PioneerPharmacy.exe");
                var helperExe = Path.Combine(a.StageDir, "Helper", "SuavoAgent.Helper.exe");
                foreach (var (path, what) in new[] { (simExe, "simulator"), (helperExe, "real Helper") })
                {
                    if (!File.Exists(path))
                    {
                        log.LogError("Staged {What} not found at {Path} — run rehearsal.ps1 first.", what, path);
                        return 3;
                    }
                }

                var simArgs = $"--variant {a.Variant}"
                    + (a.ClearSearchAfterLoad ? " --clear-search-after-load" : "")
                    + (a.NoPersistLastItem ? " --no-persist-last-item" : "");
                log.LogInformation("Launching simulator: PioneerPharmacy.exe {Args}", simArgs);
                simProc = Process.Start(new ProcessStartInfo(simExe, simArgs) { UseShellExecute = true });
                await WaitForMainWindowAsync(simProc!, TimeSpan.FromSeconds(20), ct);

                log.LogInformation("Launching REAL Helper: SuavoAgent.Helper.exe --pipe {Main} --cmd-pipe {Cmd}", mainPipe, cmdPipe);
                helperProc = Process.Start(new ProcessStartInfo(helperExe, $"--pipe {mainPipe} --cmd-pipe {cmdPipe}")
                {
                    UseShellExecute = true,
                    // Minimized so the Helper console can never sit over the sim's menu —
                    // the workflow's menu interaction is a REAL mouse click at screen coords.
                    WindowStyle = ProcessWindowStyle.Minimized,
                });
            }

            // ── REAL chain composition (PricingExecutor=UiaFirst) ────────────
            var visionDataDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "SuavoAgent");
            var visionStore = new WindowsVisionConfigurationStore();
            var visionState = VisionConfigurationRegistry.Load(
                visionStore,
                visionDataDirectory);
            if (!visionState.IsValid)
            {
                log.LogError(
                    "Protected vision registry state is invalid ({Code}); rehearsal refuses the live Helper",
                    visionState.Code);
                return 3;
            }
            var visionHandshake = new VisionConfigurationStatusProvider(
                visionState,
                visionStore,
                visionDataDirectory).EffectiveHandshake;
            await using var ipc = new IpcCommandClient(
                cmdPipe,
                loggerFactory.CreateLogger<IpcCommandClient>(),
                visionHandshake);

            // Stage 1: the REAL blind-run gate (pipe reachable + ping + interactive session).
            log.LogInformation("STAGE 1 — HelperInteractivePreflight (real gate; Helper must be in the interactive console session)");
            var preflight = await PollPreflightAsync(ipc, log, TimeSpan.FromSeconds(90), ct);
            if (preflight is null) return 3;

            // Stage 2: wait for the REAL engine to attach to the sim process, proven through
            // the REAL pricing_lookup path (not a side channel).
            if (!a.SkipWarmup)
            {
                log.LogInformation("STAGE 2 — waiting for PioneerRxUiaEngine.TryAttach (probe = real pricing_lookup)");
                if (!await WaitForAttachAsync(ipc, log, ct)) return 3;
            }

            using var db = new AgentStateDb(Path.Combine(workDir, $"rehearsal-state-{DateTime.Now:yyyyMMdd-HHmmss}.db"));
            var reader = new ExcelPricingReader(loggerFactory.CreateLogger<ExcelPricingReader>());
            var writer = new ExcelPricingWriter(loggerFactory.CreateLogger<ExcelPricingWriter>());
            var runner = new PricingJobRunner(
                reader, writer, db,
                loggerFactory.CreateLogger<PricingJobRunner>(),
                brainEvaluator: null, // prod default: Reasoning.PricingBrainEnabled=false
                interLookupDelay: TimeSpan.FromMilliseconds(a.ThrottleMs));
            var actuationGateway = new HelperActuationGateway(
                () => new IpcCommandClient(
                    cmdPipe,
                    loggerFactory.CreateLogger<IpcCommandClient>(),
                    visionHandshake),
                loggerFactory.CreateLogger<HelperActuationGateway>());
            var executor = new UiaFirstPricingJobExecutor(
                runner,
                ipc,
                db,
                actuationGateway,
                loggerFactory.CreateLogger<UiaFirstPricingJobExecutor>(),
                Options.Create(new AgentOptions
                {
                    PricingExecutor = PricingExecutorMode.UiaFirst,
                    PricingThrottleMs = a.ThrottleMs,
                }));
            log.LogInformation(
                "Pricing executor selected: UiaFirst ({Type}); throttle={ThrottleMs}ms",
                executor.GetType().Name, a.ThrottleMs);

            // Stage 3 (full mode): the REAL file-discovery chain.
            var jobId = Guid.NewGuid().ToString("N");
            string chosenPath = workbookPath;
            if (a.Mode == RunMode.Full)
            {
                log.LogInformation("STAGE 3 — real discovery: find_file → Helper FileLocatorService (pack pharmacy_rx)");
                var discoveryClient = new DiscoveryClient(loggerFactory.CreateLogger<DiscoveryClient>());
                var spec = PharmacyPresets.NdcPricingList();
                var outcome = await discoveryClient.FindAsync(ipc, Guid.NewGuid().ToString("N"), spec, ct);
                if (!outcome.Succeeded)
                {
                    log.LogError("Discovery failed: {Reason} — {Message}",
                        outcome.Failure!.ReasonCode, outcome.Failure.HumanMessage);
                    return 3;
                }
                var result = outcome.Result!;
                log.LogInformation("Discovery resolution={Resolution} best={File} confidence={Conf} tier={Tier} reason={Reason}",
                    result.Resolution,
                    result.Best?.Candidate.Candidate.FileName ?? "(none)",
                    result.Best?.Confidence.ToString("F2") ?? "-",
                    result.Best?.Tier.ToString() ?? "-",
                    result.Best?.Reason ?? "-");

                if (result.Resolution != FileDiscoveryResolution.AutoUse || result.Best is null)
                {
                    log.LogError(
                        "Discovery did not reach AutoUse (got {Resolution}). In production this becomes a " +
                        "needs_confirmation ack; for the rehearsal it is a finding — tune the workbook name/shape " +
                        "or inspect the ranker.", result.Resolution);
                    return 3;
                }

                chosenPath = result.Best.Candidate.Candidate.AbsolutePath;
                if (!IsExcelPathSafe(chosenPath, out var canonical, out var unsafeReason))
                {
                    log.LogError("Discovered path rejected by safety gate: {Reason}", unsafeReason);
                    return 3;
                }
                chosenPath = canonical;
                if (!string.Equals(chosenPath, workbookPath, StringComparison.OrdinalIgnoreCase))
                    log.LogWarning(
                        "Discovery picked {Chosen}, not the generated workbook {Expected} — another file on the " +
                        "Desktop out-scored it. Grading proceeds against the DISCOVERED file (it will likely fail).",
                        chosenPath, workbookPath);
            }

            // Stage 4: the REAL executor run (its own preflight runs again inside — the
            // executor-level blind-run gate is part of what is being rehearsed).
            log.LogInformation("STAGE 4 — UiaFirstPricingJobExecutor.RunAsync (job {JobId})", jobId);
            var jobSpec = new PricingJobSpec(jobId, chosenPath, PricingJobDefaults.NdcColumn,
                PricingJobDefaults.SupplierColumn, PricingJobDefaults.CostColumn);
            db.UpsertPricingJob(jobSpec, PricingJobStatus.Pending, 0, 0, 0);

            var sw = Stopwatch.StartNew();
            var execution = await executor.RunAsync(jobSpec, ct);
            sw.Stop();

            log.LogInformation(
                "Executor finished in {Elapsed:F0}s — mode={Mode} ok={Ok} status={Status} total={Total} completed={Completed} failed={Failed}{Halt}",
                sw.Elapsed.TotalSeconds, execution.Mode, execution.Ok, execution.Progress.Status,
                execution.Progress.TotalItems, execution.Progress.CompletedItems, execution.Progress.FailedItems,
                execution.Progress.HaltReason is null ? "" : $" haltReason={execution.Progress.HaltReason}");
            if (!execution.Ok && execution.Progress.Status != PricingJobStatus.Completed
                && execution.Progress.TotalItems == 0)
            {
                log.LogError("Chain failure before any row ran: {Error}", execution.Error);
                return 3;
            }

            // Stage 5: locate the sibling writeback and grade it.
            var output = NewestSibling(chosenPath);
            if (output is null)
            {
                log.LogError("No '{{stem}}-priced-*.xlsx' sibling found next to {Path} — writeback contract broken " +
                             "(or the job halted before writeback; see status above).", chosenPath);
                return 2;
            }
            log.LogInformation("STAGE 5 — grading sibling writeback: {Path}", output);

            var persisted = db.GetPricingResults(jobId).Count;
            DataTrace(log, chosenPath, output, execution.Progress, persisted);

            var expectations = SimCatalog.ExpectedFor(SimOptions.ParseVariant(a.Variant), a.ClearSearchAfterLoad);
            return RehearsalReport.Evaluate(chosenPath, output, expectations, Console.Out);
        }
        finally
        {
            if (!a.KeepOpen)
            {
                TryKill(helperProc, "Helper");
                TryKill(simProc, "simulator");
            }
            else
            {
                Console.WriteLine("--keep-open: simulator + Helper left running for inspection.");
            }
        }
    }

    // ── Stages ───────────────────────────────────────────────────────────────

    private static async Task<HelperPreflightResult?> PollPreflightAsync(
        IIpcCommandClient ipc, ILogger log, TimeSpan budget, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + budget;
        HelperPreflightResult last = HelperPreflightResult.Fail("not attempted");
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            last = await HelperInteractivePreflight.CheckAsync(
                ipc, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5), ct);
            if (last.Ok)
            {
                log.LogInformation("Pre-flight PASS — Helper interactive in session {Session}", last.HelperSessionId);
                return last;
            }
            log.LogInformation("Pre-flight not ready yet: {Error}", last.Error);
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
        }

        log.LogError(
            "Pre-flight FAILED after {Budget}s: {Error}\n" +
            "Checklist: (1) is this process named SuavoAgent.Core.exe and staged under the Helper's install root " +
            "(VerifyClientIsCore gate)? (2) is the VM session the CONSOLE session — RDP sessions are NOT the console " +
            "and the interactive gate correctly refuses them (use the hypervisor's console/basic view)? " +
            "(3) did the Helper start (check %ProgramData%\\SuavoAgent\\logs\\helper\\)?",
            budget.TotalSeconds, last.Error);
        return null;
    }

    /// <summary>
    /// Readiness probe through the REAL lookup path: a pricing_lookup that comes back with
    /// anything other than "PioneerRx main window not available" proves engine attach. A result
    /// like "Could not open Item → Rx Item menu" still counts as attached (wpf-menu variant).
    /// </summary>
    private static async Task<bool> WaitForAttachAsync(IIpcCommandClient ipc, ILogger log, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= 14 && !ct.IsCancellationRequested; attempt++)
        {
            var req = new IpcRequest(
                Guid.NewGuid().ToString("N"), IpcCommands.PricingLookup, 1,
                JsonSerializer.SerializeToElement(new NdcPricingRequest("warmup", 0, SimCatalog.NdcTwoSupplier)));
            var resp = await ipc.SendAsync(req, TimeSpan.FromSeconds(45), ct);
            if (resp is { Status: 200, Data: not null })
            {
                var result = JsonSerializer.Deserialize<SupplierPriceResult>(resp.Data.Value);
                if (result is not null &&
                    !string.Equals(result.ErrorMessage, "PioneerRx main window not available", StringComparison.Ordinal))
                {
                    log.LogInformation(
                        "Engine attached — warmup lookup answered (found={Found}, error={Error}). Sim window may have priced one NDC.",
                        result.Found, result.ErrorMessage ?? "none");
                    return true;
                }
            }
            log.LogInformation("Attach not ready (attempt {N}/14) — Helper's attach loop polls every 10s…", attempt);
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
        log.LogError(
            "Engine never attached to the sim. Verify the sim process is PioneerPharmacy.exe, the PMS install marker " +
            "exists, and the Helper console/logs show the attach loop running.");
        return false;
    }

    // Mirror of HeartbeatWorker.IsExcelPathSafe (private static there) — same gates:
    // .xlsx only, local absolute (no UNC), canonicalization must be a no-op.
    private static bool IsExcelPathSafe(string path, out string canonical, out string reason)
    {
        canonical = "";
        reason = "";
        var ext = Path.GetExtension(path);
        if (!string.Equals(ext, ".xlsx", StringComparison.OrdinalIgnoreCase)) { reason = "path must be .xlsx"; return false; }
        if (path.StartsWith(@"\\") || !Path.IsPathRooted(path)) { reason = "path must be local absolute"; return false; }
        canonical = Path.GetFullPath(path);
        if (!string.Equals(canonical, path, StringComparison.OrdinalIgnoreCase)) { reason = "path canonicalization mismatch"; return false; }
        return true;
    }

    private static string? NewestSibling(string sourcePath)
    {
        var dir = Path.GetDirectoryName(sourcePath)!;
        var stem = Path.GetFileNameWithoutExtension(sourcePath);
        return Directory.EnumerateFiles(dir, $"{stem}-priced-*.xlsx")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private static async Task WaitForMainWindowAsync(Process p, TimeSpan budget, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + budget;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            try
            {
                p.Refresh();
                if (p.MainWindowHandle != IntPtr.Zero) return;
            }
            catch { /* process warming up */ }
            await Task.Delay(250, ct);
        }
    }

    private static void TryKill(Process? p, string what)
    {
        if (p is null) return;
        try
        {
            if (!p.HasExited) { p.Kill(entireProcessTree: true); Console.WriteLine($"Stopped {what} (pid {p.Id})."); }
        }
        catch { /* already gone */ }
    }

    private static void DataTrace(
        ILogger log, string source, string output, PricingJobProgress progress, int persistedResults)
    {
        log.LogInformation(
            "DATA TRACE — Excel rows read: {Total} (reader) → IPC pricing_lookup per row (runner) → " +
            "UIA drive of Edit Rx Item (PricingWorkflow on real Helper) → SQLite results persisted: {Persisted} " +
            "(AgentStateDb) → sibling writeback: {Output} (writer). Source untouched: {Source}",
            progress.TotalItems, persistedResults, Path.GetFileName(output), Path.GetFileName(source));
    }

    private static void Banner(Args a)
    {
        Console.WriteLine("──────────────────────────────────────────────────────────────────────────────");
        Console.WriteLine(" PioneerRx DRESS REHEARSAL — real pricing chain vs PioneerRx-shaped simulator");
        Console.WriteLine($"   mode={a.Mode}  variant={a.Variant}  clearSearchAfterLoad={a.ClearSearchAfterLoad}");
        Console.WriteLine($"   persistLastItem={!a.NoPersistLastItem}  throttle={a.ThrottleMs}ms  stage={a.StageDir}");
        Console.WriteLine(" Chain: preflight → (discovery) → UiaFirstPricingJobExecutor → PricingJobRunner");
        Console.WriteLine("        → IpcCommandClient ⇄ SuavoAgent.Helper.exe (IpcCommandServer → PricingWorkflow");
        Console.WriteLine("        → PioneerRxUiaEngine/UIA2) → AgentStateDb → ExcelPricingWriter sibling file");
        Console.WriteLine("──────────────────────────────────────────────────────────────────────────────");
    }

    // ── Args ─────────────────────────────────────────────────────────────────

    private enum RunMode { Quick, Full }

    private sealed record Args(
        RunMode Mode,
        string Variant,
        bool ClearSearchAfterLoad,
        bool NoPersistLastItem,
        string StageDir,
        string? WorkbookPath,
        int ThrottleMs,
        bool NoLaunch,
        string? CmdPipeOverride,
        bool KeepOpen,
        bool SkipWarmup,
        bool IgnorePmsMarker)
    {
        public static Args? Parse(string[] args)
        {
            var mode = RunMode.Quick;
            var variant = "faithful";
            bool clearSearch = false, noPersist = false, noLaunch = false, keepOpen = false, skipWarmup = false, ignoreMarker = false;
            string? workbook = null, cmdPipe = null;
            // Default stage root = two levels up from this exe (stage/Core/SuavoAgent.Core.exe).
            var stage = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", ".."));
            var throttle = 1500; // AgentOptions.PricingThrottleMs default

            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--mode" when i + 1 < args.Length:
                        mode = args[++i].Equals("full", StringComparison.OrdinalIgnoreCase) ? RunMode.Full : RunMode.Quick;
                        break;
                    case "--variant" when i + 1 < args.Length: variant = args[++i]; break;
                    case "--clear-search-after-load": clearSearch = true; break;
                    case "--no-persist-last-item": noPersist = true; break;
                    case "--stage" when i + 1 < args.Length: stage = Path.GetFullPath(args[++i]); break;
                    case "--workbook" when i + 1 < args.Length: workbook = args[++i]; break;
                    case "--throttle-ms" when i + 1 < args.Length && int.TryParse(args[i + 1], out var t): throttle = t; i++; break;
                    case "--no-launch": noLaunch = true; break;
                    case "--cmd-pipe" when i + 1 < args.Length: cmdPipe = args[++i]; break;
                    case "--keep-open": keepOpen = true; break;
                    case "--skip-warmup": skipWarmup = true; break;
                    case "--ignore-pms-marker": ignoreMarker = true; break;
                    case "--help" or "-h":
                        PrintHelp();
                        return null;
                    default:
                        Console.Error.WriteLine($"Unknown arg '{args[i]}' (use --help)");
                        return null;
                }
            }

            try { SimOptions.ParseVariant(variant); }
            catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); return null; }

            return new Args(mode, variant, clearSearch, noPersist, stage, workbook, throttle,
                noLaunch, cmdPipe, keepOpen, skipWarmup, ignoreMarker);
        }

        private static void PrintHelp() => Console.WriteLine("""
            PioneerRx dress rehearsal — runs the REAL UIA pricing chain against the simulator.

            usage (from the staged layout; see rehearsal.ps1):
              SuavoAgent.Core.exe [--mode quick|full] [--variant <v>] [options]

              --mode quick            9-row workbook, direct path (default)
              --mode full             55-row workbook on the Desktop + REAL find_file discovery
              --variant <v>           faithful | renamed-cost | slow-grid | glacial-grid |
                                      wpf-menu | currency-cells | virtual-depth
              --clear-search-after-load   sim clears Quick Search on load (DNU guard becomes reachable)
              --no-persist-last-item      sim does not re-show the last item on reopen
              --workbook <path>       use an existing workbook instead of generating one
              --throttle-ms <int>     inter-NDC delay (default 1500 — prod default)
              --stage <dir>           stage root (default: ../../ from this exe)
              --no-launch             do not spawn sim/Helper (requires --cmd-pipe <name>)
              --cmd-pipe <name>       Helper command pipe name when --no-launch
              --keep-open             leave sim + Helper running after grading
              --skip-warmup           skip the attach-readiness probe
              --ignore-pms-marker     proceed without the PioneerRx install marker

            exit codes: 0 conformance pass · 2 conformance fail (workflow bug if sim faithful)
                        3 chain/setup failure · 64 bad args
            """);
    }
}
