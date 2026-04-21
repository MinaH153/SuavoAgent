using FlaUI.UIA2;
using Serilog;
using SuavoAgent.Contracts.Behavioral;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Helper;
using SuavoAgent.Helper.Behavioral;
using SuavoAgent.Helper.SystemObservers;
using SuavoAgent.Helper.Workflows;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Is(Environment.GetEnvironmentVariable("SUAVO_DEBUG") == "1"
        ? Serilog.Events.LogEventLevel.Debug
        : Serilog.Events.LogEventLevel.Information)
    .WriteTo.Console()
    .WriteTo.File(
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SuavoAgent", "logs", "helper-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14)
    .CreateLogger();

try
{
    Log.Information("SuavoAgent.Helper starting");

    var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

    // H-10: resolve pipe name from --pipe arg (written by Core, passed by Broker).
    // Fail-closed if either arg is missing — a literal default name would be squat-able
    // by any local process before Helper starts.
    string? pipeName = null;
    string? cmdPipeName = null;
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i] == "--pipe") { pipeName = args[i + 1]; }
        if (args[i] == "--cmd-pipe") { cmdPipeName = args[i + 1]; }
    }

    if (string.IsNullOrEmpty(pipeName) || string.IsNullOrEmpty(cmdPipeName))
    {
        Log.Fatal("Helper: missing --pipe or --cmd-pipe arg (both required, no defaults). " +
                  "Broker must pass nonce-scoped pipe names — exiting.");
        Environment.Exit(2);
        return;
    }

    using var pioneer = new PioneerRxUiaEngine(Log.Logger);
    using var ipcClient = new IpcPipeClient(pipeName, Log.Logger);
    var pricingWorkflow = new PricingWorkflow(pioneer, Log.Logger);

    // Vision pipeline — operator opt-in via ProgramData\SuavoAgent\vision.json.
    // Returns null (no vision) when disabled, which is the default.
    var visionController = SuavoAgent.Helper.Vision.VisionBootstrap.TryBuild(Log.Logger);

    // File discovery — runs in Helper (interactive user session) because
    // Core runs as LocalSystem and doesn't see the user's Desktop/
    // Documents. Heuristic-only ranker in v3.13; LLM tier plugs in later.
    var fileLocator = new SuavoAgent.Core.Discovery.FileLocatorService(
        enumerator: new SuavoAgent.Core.Discovery.DefaultFileEnumerator(),
        scorer: new SuavoAgent.Core.Discovery.FilenameHeuristicScorer(),
        sampler: new SuavoAgent.Core.Discovery.TabularShapeSampler(),
        ranker: new SuavoAgent.Core.Discovery.HeuristicOnlyRanker());

    using var cmdServer = new IpcCommandServer(cmdPipeName, pricingWorkflow, Log.Logger, visionController, fileLocator);
    cmdServer.Start(cts.Token);

    const int maxAttachRetries = 30; // 30 × 10s = 5 minutes of retrying
    int attachFailures = 0;
    bool attached = false;

    // Behavioral observer state — created on attach, torn down on detach
    BehavioralEventBuffer? eventBuffer = null;
    UiaTreeObserver? treeObserver = null;
    UiaInteractionObserver? interactionObserver = null;
    KeyboardCategoryHook? keyboardHook = null;
    CancellationTokenSource? treeObserverCts = null;
    CancellationTokenSource? keyboardPumpCts = null;
    Thread? keyboardPumpThread = null;
    string pharmacySalt = ""; // fetched from Core via IPC handshake

    async Task<string> FetchPharmacySaltAsync()
    {
        try
        {
            if (!ipcClient.IsConnected)
                await ipcClient.ConnectAsync(TimeSpan.FromSeconds(5), cts.Token);
            var response = await ipcClient.SendAsync(
                new IpcRequest(Guid.NewGuid().ToString("N"), IpcCommands.GetPharmacySalt, 1, null), cts.Token);
            if (response is { Status: 200, Data: not null })
                return response.Data.Value.GetString() ?? "";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Cannot fetch pharmacySalt from Core — halting behavioral observation (HIPAA fail-closed)");
            cts.Cancel();
        }
        return "";
    }

    void StartBehavioralObservers()
    {
        StopBehavioralObservers();

        var salt = pharmacySalt;

        eventBuffer = new BehavioralEventBuffer(
            capacity: 500,
            batchSize: 50,
            flushAction: async events =>
            {
                var json = System.Text.Json.JsonSerializer.Serialize(events);
                await ipcClient.TrySendAsync(IpcCommands.BehavioralEvents, json, cts.Token);
            });

        treeObserver = new UiaTreeObserver(salt, eventBuffer, Log.Logger);

        interactionObserver = new UiaInteractionObserver(
            new UIA2Automation(),
            salt, eventBuffer, Log.Logger,
            triggerTreeResnapshot: () =>
            {
                // Fire a manual tree walk on structure change
                if (pioneer.MainWindow is { } win)
                    treeObserver.WalkTree(win);
            });

        keyboardHook = new KeyboardCategoryHook(eventBuffer, Log.Logger, pioneer.ProcessId);

        // UiaTreeObserver runs as an async loop — give it a linked token
        treeObserverCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        _ = Task.Run(() => treeObserver.RunAsync(
            () => pioneer.MainWindow, treeObserverCts.Token), treeObserverCts.Token);

        // Subscribe interaction observer to the current PMS window
        if (pioneer.MainWindow is { } window)
            interactionObserver.Subscribe(window);

        // WH_KEYBOARD_LL requires a message pump on the installing thread.
        // Install hook + pump on a dedicated STA thread so callbacks actually fire.
        keyboardPumpCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        var pumpCt = keyboardPumpCts.Token;
        var hook = keyboardHook; // capture for thread closure
        keyboardPumpThread = new Thread(() => NativeMessagePump.RunHookPump(hook, pumpCt));
        keyboardPumpThread.IsBackground = true;
        keyboardPumpThread.SetApartmentState(ApartmentState.STA);
        keyboardPumpThread.Start();

        Log.Information("Behavioral observers started (PID {Pid})", pioneer.ProcessId);
    }

    void StopBehavioralObservers()
    {
        // Stop message pump thread first — this calls Uninstall() in its finally block
        keyboardPumpCts?.Cancel();
        keyboardPumpThread?.Join(TimeSpan.FromSeconds(2));
        keyboardPumpCts?.Dispose();
        keyboardPumpCts = null;
        keyboardPumpThread = null;

        keyboardHook?.Dispose();
        keyboardHook = null;

        interactionObserver?.Dispose();
        interactionObserver = null;

        treeObserverCts?.Cancel();
        treeObserverCts?.Dispose();
        treeObserverCts = null;
        treeObserver = null;

        eventBuffer?.Dispose();
        eventBuffer = null;
    }

    // ── System observers — always running, no PMS dependency ──
    pharmacySalt = await FetchPharmacySaltAsync();

    var systemBuffer = new BehavioralEventBuffer(
        capacity: 200,
        batchSize: 20,
        flushAction: async events =>
        {
            var json = System.Text.Json.JsonSerializer.Serialize(events);
            await ipcClient.TrySendAsync(IpcCommands.SystemEvents, json, cts.Token);
        });

    var foregroundTracker = new ForegroundTracker(systemBuffer, pharmacySalt, Log.Logger);
    var stationProfiler = new StationProfiler(systemBuffer, pharmacySalt, Log.Logger);
    var sessionObserver = new UserSessionObserver(systemBuffer, pharmacySalt, Log.Logger);

    // Station profile — one-shot on startup
    stationProfiler.CaptureProfile();

    // Foreground tracker — runs as async loop
    var fgTrackerCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
    _ = Task.Run(() => foregroundTracker.RunAsync(fgTrackerCts.Token), fgTrackerCts.Token);

    Log.Information("System observers started (foreground tracker, station profiler, session observer)");

    // Employee disclosure indicator (CT/DE/NY compliance)
    var trayIndicator = new SuavoAgent.Helper.SystemTray.TrayIndicator(Log.Logger);
    trayIndicator.Start();

    // ── App intelligence observers ──
    var adapterDir = Path.Combine(AppContext.BaseDirectory, "adapters");
    var industryAdapter = SuavoAgent.Core.Config.IndustryAdapter.LoadForIndustry("pharmacy", adapterDir);

    var browserObserver = new SuavoAgent.Helper.SystemObservers.BrowserDomainObserver(
        systemBuffer, pharmacySalt, industryAdapter.ClassifyDomain, Log.Logger);

    var printObserver = new SuavoAgent.Helper.SystemObservers.PrintEventObserver(
        systemBuffer, pharmacySalt, Log.Logger);

    var printObsCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
    _ = Task.Run(() => printObserver.RunAsync(printObsCts.Token), printObsCts.Token);

    Log.Information("App intelligence observers started (browser domains, print events)");

    var spreadsheetObserver = new SuavoAgent.Helper.SystemObservers.SpreadsheetStructureObserver(
        systemBuffer, pharmacySalt, Log.Logger);
    var multiAppUia = new SuavoAgent.Helper.SystemObservers.MultiAppUiaObserver(
        systemBuffer, pharmacySalt, Log.Logger);
    Log.Information("Spreadsheet and multi-app UIA observers initialized");

    // Wire ForegroundTracker → app-specific observers
    foregroundTracker.OnAppFocusChanged((processName, domainOrTitle) =>
    {
        if (BrowserDomainObserver.IsBrowserProcess(processName) && domainOrTitle != null)
            browserObserver.OnDomainDetected(domainOrTitle);

        if (SpreadsheetStructureObserver.IsSpreadsheetProcess(processName))
        {
            // Spreadsheet detected — capture metadata on next available title
        }

        multiAppUia.OnAppFocused(processName, null); // no raw titles
    });

    // Retry attachment — PioneerRx may not be running when Helper starts
    while (!cts.Token.IsCancellationRequested && !attached)
    {
        if (pioneer.TryAttach())
        {
            attached = true;
            attachFailures = 0;
            Log.Information("Attached to PioneerRx PID {Pid}", pioneer.ProcessId);

            // Report success to Core via IPC
            await ipcClient.TrySendAsync("pioneer_attached", null, cts.Token);

            // Fetch pharmacy salt from Core before starting observers
            pharmacySalt = await FetchPharmacySaltAsync();

            // Wire behavioral observers
            StartBehavioralObservers();
        }
        else
        {
            attachFailures++;
            Log.Warning("PioneerRx not found (attempt {Attempt}/{Max})",
                attachFailures, maxAttachRetries);

            // Report persistent failure to Core after threshold
            if (attachFailures % 6 == 0) // every 60 seconds
            {
                await ipcClient.TrySendAsync("pioneer_attach_failed",
                    System.Text.Json.JsonSerializer.Serialize(new
                    {
                        consecutiveFailures = attachFailures,
                        maxRetries = maxAttachRetries
                    }), cts.Token);
            }

            if (attachFailures >= maxAttachRetries)
            {
                Log.Warning("PioneerRx not found after {Max} attempts — will restart via Broker", maxAttachRetries);
                await ipcClient.TrySendAsync("pioneer_attach_exhausted",
                    System.Text.Json.JsonSerializer.Serialize(new { totalAttempts = attachFailures }), cts.Token);
                // Exit with non-zero so Broker relaunches us
                Environment.Exit(1);
            }

            await Task.Delay(TimeSpan.FromSeconds(10), cts.Token);
        }
    }

    // Health monitoring loop
    while (!cts.Token.IsCancellationRequested && attached)
    {
        var health = pioneer.CheckHealth();
        if (!health.WindowFound)
        {
            Log.Warning("PioneerRx window lost — stopping observers, attempting re-attach");
            StopBehavioralObservers();
            attached = false;
            attachFailures = 0;

            // Re-enter attachment loop
            while (!cts.Token.IsCancellationRequested && !attached)
            {
                if (pioneer.TryAttach())
                {
                    attached = true;
                    Log.Information("Re-attached to PioneerRx PID {Pid}", pioneer.ProcessId);
                    await ipcClient.TrySendAsync("pioneer_reattached", null, cts.Token);

                    // Re-fetch salt in case session rotated
                    pharmacySalt = await FetchPharmacySaltAsync();

                    // Re-wire behavioral observers for new window
                    StartBehavioralObservers();
                }
                else
                {
                    attachFailures++;
                    if (attachFailures >= maxAttachRetries)
                    {
                        Log.Warning("PioneerRx re-attach failed after {Max} attempts — will restart via Broker", maxAttachRetries);
                        await ipcClient.TrySendAsync("pioneer_attach_exhausted",
                            System.Text.Json.JsonSerializer.Serialize(new { totalAttempts = attachFailures }), cts.Token);
                        Environment.Exit(1);
                    }
                    await Task.Delay(TimeSpan.FromSeconds(10), cts.Token);
                }
            }
            if (!attached) break;
        }

        Log.Debug("PioneerRx health: Window={Window}, MenuBar={Menu}",
            health.WindowFound, health.MenuBarFound);
        await Task.Delay(TimeSpan.FromSeconds(10), cts.Token);
    }

    // Cleanup system observers
    trayIndicator?.Dispose();
    fgTrackerCts?.Cancel();
    foregroundTracker?.Dispose();
    sessionObserver?.Dispose();
    printObsCts?.Cancel();
    printObserver?.Dispose();
    systemBuffer?.Dispose();

    // Final cleanup
    StopBehavioralObservers();
}
catch (OperationCanceledException) { }
catch (Exception ex)
{
    Log.Fatal(ex, "Helper terminated unexpectedly");
    Environment.Exit(1);
}
finally
{
    Log.CloseAndFlush();
}
