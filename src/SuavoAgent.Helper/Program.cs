using FlaUI.UIA2;
using Serilog;
using SuavoAgent.Contracts.Behavioral;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Helper;
using SuavoAgent.Helper.Behavioral;

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

    using var pioneer = new PioneerRxUiaEngine(Log.Logger);
    using var ipcClient = new IpcPipeClient("SuavoAgent", Log.Logger);

    const int maxAttachRetries = 30; // 30 × 10s = 5 minutes of retrying
    int attachFailures = 0;
    bool attached = false;

    // Behavioral observer state — created on attach, torn down on detach
    BehavioralEventBuffer? eventBuffer = null;
    UiaTreeObserver? treeObserver = null;
    UiaInteractionObserver? interactionObserver = null;
    KeyboardCategoryHook? keyboardHook = null;
    CancellationTokenSource? treeObserverCts = null;
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
            Log.Warning(ex, "Failed to fetch pharmacySalt from Core — using empty salt");
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

        keyboardHook.Install();

        Log.Information("Behavioral observers started (PID {Pid})", pioneer.ProcessId);
    }

    void StopBehavioralObservers()
    {
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

    // Retry attachment — PioneerRx may not be running when Helper starts
    while (!cts.Token.IsCancellationRequested && !attached)
    {
        if (pioneer.TryAttach())
        {
            attached = true;
            attachFailures = 0;
            Log.Information("Attached to PioneerRx: {Title}", pioneer.WindowTitle);

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
                Log.Warning("PioneerRx not found after {Max} attempts — exiting", maxAttachRetries);
                await ipcClient.TrySendAsync("pioneer_attach_exhausted",
                    System.Text.Json.JsonSerializer.Serialize(new
                    {
                        totalAttempts = attachFailures
                    }), cts.Token);
                break;
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
                    Log.Information("Re-attached to PioneerRx: {Title}", pioneer.WindowTitle);
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
                        Log.Warning("PioneerRx re-attach failed after {Max} attempts", maxAttachRetries);
                        break;
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
