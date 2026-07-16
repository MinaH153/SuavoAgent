using System.Text.Json;
using Serilog;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Contracts.Vision;
using SuavoAgent.Core.Vision;
using SuavoAgent.Helper.Actuation;
using SuavoAgent.Helper.Presence;
using SuavoAgent.Helper.Vision;
using SuavoAgent.Helper.Workflows;
using Xunit;

namespace SuavoAgent.Helper.Tests;

/// <summary>
/// Runs the authenticated-command dispatch surface over a real local pipe.
/// Non-Windows test hosts deliberately exercise the framing and dispatch
/// boundary while the production Windows build adds its service-SID proof.
/// No desktop input is possible because actuation is disabled and the
/// command handler is intentionally absent from this server instance.
/// </summary>
public sealed class IpcCommandServerIntegrationTests
{
    [Fact]
    public async Task RealPipe_RoutesSafetyCriticalCommandsAndFailsClosed()
    {
        using var logger = new LoggerConfiguration().CreateLogger();
        using var engine = new PioneerRxUiaEngine(logger);
        var actuationGate = new ActuationGate(new ActuationConfig
        {
            Enabled = false,
            DryRun = true,
        }, logger);
        var pricing = new PricingWorkflow(engine, actuationGate, logger);
        var visionConfiguration = new VisionConfigurationLoadResult(
            IsValid: true,
            IsMissing: true,
            Code: "vision_registry_state_missing",
            EffectiveOptions: VisionOptionsSnapshot.DisabledDefault());
        var visionGate = new VisionGenerationGate(visionConfiguration);
        var visionStatus = new VisionRuntimeStatusTracker(visionConfiguration);
        var presence = new PresencePreferenceStore(PresencePreferences.SafeDefault());
        var pipeName = $"sa_cmd_{Guid.NewGuid():N}";
        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var server = new IpcCommandServer(
            pipeName,
            pricing,
            logger,
            visionGate,
            presenceStore: presence,
            visionRuntimeStatus: visionStatus,
            onWedgedDispatch: () => throw new Xunit.Sdk.XunitException(
                "No bounded test dispatch may wedge."));
        server.Start(shutdown.Token);
        using var client = new IpcPipeClient(
            pipeName,
            logger,
            requestTimeout: TimeSpan.FromSeconds(2));
        Assert.True(await client.ConnectAsync(TimeSpan.FromSeconds(3), shutdown.Token));

        var ping = await SendAsync(client, IpcCommands.Ping, data: null, shutdown.Token);
        Assert.Equal(IpcStatus.Ok, ping.Status);
        Assert.NotNull(ping.Data);
        var pingInfo = HelperPingInfo.TryParse(ping.Data);
        Assert.NotNull(pingInfo);
        Assert.Equal(
            VisionRuntimeCodes.VisionDisabled,
            pingInfo!.VisionRuntime?.Code);

        // Regression: structural learned-selector clicks must enter the same
        // actuation boundary as label clicks. Falling through to
        // unknown_command would make learned workflow replay non-functional.
        var structuralClick = await SendAsync(
            client,
            ActuationIpcCommands.ClickBySignature,
            JsonSerializer.SerializeToElement(new ClickBySignatureRequest(
                "Button", "num7Button", null, "calculator", 1, DryRun: true)),
            shutdown.Token);
        Assert.Equal("actuation_unavailable", structuralClick.Error?.Code);

        var labelClick = await SendAsync(
            client,
            ActuationIpcCommands.ClickByLabel,
            JsonSerializer.SerializeToElement(new ClickByLabelRequest(
                "Seven", "calculator", "exact", 1, DryRun: true)),
            shutdown.Token);
        Assert.Equal("actuation_unavailable", labelClick.Error?.Code);

        var setHidden = await SendAsync(
            client,
            "presence.set_visible",
            JsonSerializer.SerializeToElement(new { visible = false }),
            shutdown.Token);
        Assert.Equal(IpcStatus.Ok, setHidden.Status);
        Assert.False(presence.Current.CursorVisible);
        Assert.False(setHidden.Data!.Value.GetProperty("visible").GetBoolean());

        var badPresence = await SendAsync(
            client,
            "presence.set_visible",
            JsonSerializer.SerializeToElement(new { visible = "no" }),
            shutdown.Token);
        Assert.Equal(IpcStatus.BadRequest, badPresence.Status);
        Assert.Equal("bad_request", badPresence.Error?.Code);

        var missingCursor = await SendAsync(
            client,
            IpcCommands.IntentCursor,
            JsonSerializer.SerializeToElement(new { }),
            shutdown.Token);
        Assert.Equal("intent_cursor_unavailable", missingCursor.Error?.Code);

        var missingPioneer = await SendAsync(
            client,
            PioneerRxActuationIpcCommands.Click,
            JsonSerializer.SerializeToElement(new { }),
            shutdown.Token);
        Assert.Equal("pioneerrx_unavailable", missingPioneer.Error?.Code);

        var missingVision = await SendAsync(
            client,
            IpcCommands.CaptureScreen,
            data: null,
            shutdown.Token);
        Assert.Equal("vision_unavailable", missingVision.Error?.Code);

        var missingSandbox = await SendAsync(
            client,
            IpcCommands.CaptureScreen,
            JsonSerializer.SerializeToElement(new { targetProcess = "sandbox" }),
            shutdown.Token);
        Assert.Equal("sandbox_driver_unavailable", missingSandbox.Error?.Code);

        var missingLocator = await SendAsync(
            client,
            IpcCommands.FindFile,
            JsonSerializer.SerializeToElement(new { }),
            shutdown.Token);
        Assert.Equal("locator_unavailable", missingLocator.Error?.Code);

        var unconfirmedPricing = await SendAsync(
            client,
            IpcCommands.PricingLookup,
            JsonSerializer.SerializeToElement(new NdcPricingRequest(
                "job", 1, "00093505698")),
            shutdown.Token);
        Assert.Equal("vision_generation_unconfirmed", unconfirmedPricing.Error?.Code);

        var invalidHandshake = await SendAsync(
            client,
            IpcCommands.VisionStateHandshake,
            JsonSerializer.SerializeToElement(new { generation = 0 }),
            shutdown.Token);
        Assert.Equal(IpcStatus.BadRequest, invalidHandshake.Status);
        Assert.Equal("vision_handshake_fields_invalid", invalidHandshake.Error?.Code);

        var exactHandshake = await SendAsync(
            client,
            IpcCommands.VisionStateHandshake,
            JsonSerializer.SerializeToElement(new VisionStateHandshake(
                VisionStateHandshake.CurrentSchemaVersion,
                visionGate.LocalGeneration,
                visionGate.LocalDigest)),
            shutdown.Token);
        Assert.Equal(IpcStatus.Ok, exactHandshake.Status);
        Assert.True(exactHandshake.Data!.Value.GetProperty("matched").GetBoolean());

        var missingPricingData = await SendAsync(
            client,
            IpcCommands.PricingLookup,
            data: null,
            shutdown.Token);
        Assert.Equal("bad_request", missingPricingData.Error?.Code);

        var gatedPricing = await SendAsync(
            client,
            IpcCommands.PricingLookup,
            JsonSerializer.SerializeToElement(new NdcPricingRequest(
                "job", 1, "00093505698")),
            shutdown.Token);
        Assert.Equal(IpcStatus.Ok, gatedPricing.Status);
        var pricingResult = JsonSerializer.Deserialize<SupplierPriceResult>(
            gatedPricing.Data!.Value.GetRawText());
        Assert.NotNull(pricingResult);
        Assert.False(pricingResult!.Found);

        var unknown = await SendAsync(
            client,
            "unreviewed.command",
            data: null,
            shutdown.Token);
        Assert.Equal("unknown_command", unknown.Error?.Code);

        shutdown.Cancel();
    }

    [Fact]
    public async Task VisionCaptureRequiresConnectionBoundGenerationAndForegroundTruth()
    {
        using var logger = new LoggerConfiguration().CreateLogger();
        using var engine = new PioneerRxUiaEngine(logger);
        var actuationGate = new ActuationGate(new ActuationConfig
        {
            Enabled = false,
            DryRun = true,
        }, logger);
        var pricing = new PricingWorkflow(engine, actuationGate, logger);
        var visionConfiguration = new VisionConfigurationLoadResult(
            IsValid: true,
            IsMissing: true,
            Code: "vision_registry_state_missing",
            EffectiveOptions: VisionOptionsSnapshot.DisabledDefault());
        var visionGate = new VisionGenerationGate(visionConfiguration);
        var capture = new MutableCapture
        {
            Result = new ScreenBytes([1, 2, 3], 100, 80, DateTimeOffset.UtcNow),
        };
        var store = new RecordingStore();
        var extractor = new MutableExtractor
        {
            Result = new ScreenFrame
            {
                Id = "scrubbed-frame",
                CapturedAt = DateTimeOffset.UtcNow,
                Width = 100,
                Height = 80,
                ExtractorId = "test-scrubbed",
            },
        };
        var controller = new ScreenCaptureController(capture, store, extractor, logger);
        var foreground = false;
        var pipeName = $"sa_cmd_{Guid.NewGuid():N}";
        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var server = new IpcCommandServer(
            pipeName,
            pricing,
            logger,
            visionGate,
            vision: controller,
            isPmsForeground: () => foreground,
            onWedgedDispatch: () => throw new Xunit.Sdk.XunitException(
                "No bounded test dispatch may wedge."));
        server.Start(shutdown.Token);

        using var secondClient = new IpcPipeClient(
            pipeName,
            logger,
            requestTimeout: TimeSpan.FromSeconds(5));

        using (var firstClient = new IpcPipeClient(
                   pipeName,
                   logger,
                   requestTimeout: TimeSpan.FromSeconds(5)))
        {
            Assert.True(await firstClient.ConnectAsync(
                TimeSpan.FromSeconds(3),
                shutdown.Token));

            var beforeHandshake = await SendAsync(
                firstClient,
                IpcCommands.CaptureScreen,
                data: null,
                shutdown.Token);
            Assert.Equal(
                "vision_generation_unconfirmed",
                beforeHandshake.Error?.Code);

            var handshake = await SendAsync(
                firstClient,
                IpcCommands.VisionStateHandshake,
                JsonSerializer.SerializeToElement(new VisionStateHandshake(
                    VisionStateHandshake.CurrentSchemaVersion,
                    visionGate.LocalGeneration,
                    visionGate.LocalDigest)),
                shutdown.Token);
            Assert.Equal(IpcStatus.Ok, handshake.Status);

            var backgroundCapture = await SendAsync(
                firstClient,
                IpcCommands.CaptureScreen,
                data: null,
                shutdown.Token);
            Assert.Equal("not_foreground", backgroundCapture.Error?.Code);
            Assert.Equal(0, capture.CaptureCalls);

            foreground = true;
            var success = await SendAsync(
                firstClient,
                IpcCommands.CaptureScreen,
                data: null,
                shutdown.Token);
            Assert.Equal(IpcStatus.Ok, success.Status);
            Assert.Equal("stored-screen", success.Data!.Value
                .GetProperty("storageId").GetString());
            Assert.Equal("scrubbed-frame", success.Data.Value
                .GetProperty("frame").GetProperty("Id").GetString());
            Assert.DoesNotContain(
                "AQID",
                success.Data.Value.GetRawText(),
                StringComparison.Ordinal);

            capture.Result = null;
            var nullCapture = await SendAsync(
                firstClient,
                IpcCommands.CaptureScreen,
                data: null,
                shutdown.Token);
            Assert.Equal("capture_failed", nullCapture.Error?.Code);

            capture.Throw = true;
            var failedCapture = await SendAsync(
                firstClient,
                IpcCommands.CaptureScreen,
                data: null,
                shutdown.Token);
            Assert.Equal("capture_error", failedCapture.Error?.Code);

            // The successor listener must already be accepting while the first connection
            // is active. Otherwise this connect can land in the retiring OS listener backlog
            // and receive EOF when firstClient is disposed.
            Assert.True(await secondClient.ConnectAsync(
                TimeSpan.FromSeconds(3),
                shutdown.Token));
        }

        // A new authenticated connection must reset the previous connection's
        // generation proof before accepting any machine-vision request.
        var afterReconnect = await SendAsync(
            secondClient,
            IpcCommands.CaptureScreen,
            data: null,
            shutdown.Token);
        Assert.Equal("vision_generation_unconfirmed", afterReconnect.Error?.Code);

        shutdown.Cancel();
    }

    [Fact]
    public async Task ShutdownCancelsActiveAndPendingListenersWithoutStranding()
    {
        using var logger = new LoggerConfiguration().CreateLogger();
        using var engine = new PioneerRxUiaEngine(logger);
        var pricing = new PricingWorkflow(
            engine,
            new ActuationGate(new ActuationConfig
            {
                Enabled = false,
                DryRun = true,
            }, logger),
            logger);
        var visionGate = new VisionGenerationGate(new VisionConfigurationLoadResult(
            IsValid: true,
            IsMissing: true,
            Code: "vision_registry_state_missing",
            EffectiveOptions: VisionOptionsSnapshot.DisabledDefault()));
        var pipeName = $"sa_cmd_{Guid.NewGuid():N}";
        using var shutdown = new CancellationTokenSource();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var server = new IpcCommandServer(
            pipeName,
            pricing,
            logger,
            visionGate,
            onWedgedDispatch: () => throw new Xunit.Sdk.XunitException(
                "No bounded test dispatch may wedge."));
        server.Start(shutdown.Token);

        using var activeClient = new IpcPipeClient(pipeName, logger);
        using var pendingClient = new IpcPipeClient(pipeName, logger);
        Assert.True(await activeClient.ConnectAsync(
            TimeSpan.FromSeconds(3), timeout.Token));
        Assert.True(await pendingClient.ConnectAsync(
            TimeSpan.FromSeconds(3), timeout.Token));

        shutdown.Cancel();
        await server.Completion.WaitAsync(TimeSpan.FromSeconds(3), timeout.Token);

        Assert.True(server.Completion.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task RealPipeCarriesActuationSemanticResultsWithoutBypassingLocalGate()
    {
        using var logger = new LoggerConfiguration().CreateLogger();
        using var engine = new PioneerRxUiaEngine(logger);
        var config = new ActuationConfig
        {
            Enabled = false,
            DryRun = true,
            DefaultUiaTimeout = TimeSpan.FromMilliseconds(1),
            DefaultPerKeyDelayMs = 0,
            DefaultInterChordDelayMs = 0,
        };
        var gate = new ActuationGate(config, logger);
        var driver = new SendInputDriver(gate, config, logger);
        using var resolver = new UiaLabelResolver(logger);
        using var signatureResolver = new UiaSignatureResolver(logger);
        var actuation = new ActuationCommandHandler(
            gate,
            driver,
            resolver,
            config,
            logger,
            signatureResolver);
        var processTrust = new PioneerRxProcessTrustVerifier(
            PioneerRxApprovalLoadResult.Denied("test_no_local_approval"));
        var pioneer = new PioneerRxCommandHandler(
            gate,
            driver,
            resolver,
            config,
            PioneerRxConfig.SafeDefault(),
            processTrust,
            logger);
        var pricing = new PricingWorkflow(engine, gate, logger);
        var visionConfiguration = new VisionConfigurationLoadResult(
            true,
            true,
            "vision_registry_state_missing",
            VisionOptionsSnapshot.DisabledDefault());
        var pipeName = $"sa_cmd_{Guid.NewGuid():N}";
        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var server = new IpcCommandServer(
            pipeName,
            pricing,
            logger,
            new VisionGenerationGate(visionConfiguration),
            actuation: actuation,
            pioneerRx: pioneer,
            onWedgedDispatch: () => throw new Xunit.Sdk.XunitException(
                "No bounded test dispatch may wedge."));
        server.Start(shutdown.Token);
        using var client = new IpcPipeClient(
            pipeName,
            logger,
            requestTimeout: TimeSpan.FromSeconds(2));
        Assert.True(await client.ConnectAsync(TimeSpan.FromSeconds(3), shutdown.Token));

        var stateResponse = await SendAsync(
            client,
            ActuationIpcCommands.GetState,
            data: null,
            shutdown.Token);
        Assert.Equal(IpcStatus.Ok, stateResponse.Status);
        var state = JsonSerializer.Deserialize<ActuationGateState>(
            stateResponse.Data!.Value.GetRawText());
        Assert.NotNull(state);
        Assert.False(state!.Enabled);
        Assert.True(state.DryRun);

        var structuralClick = await SendAsync(
            client,
            ActuationIpcCommands.ClickBySignature,
            JsonSerializer.SerializeToElement(new ClickBySignatureRequest(
                "Button", "num7Button", null, "calculator", 1,
                DryRun: true)),
            shutdown.Token);
        Assert.Equal(IpcStatus.Ok, structuralClick.Status);
        var clickResult = JsonSerializer.Deserialize<ActuationResult>(
            structuralClick.Data!.Value.GetRawText());
        Assert.NotNull(clickResult);
        Assert.Equal(ActuationRejectionCodes.GateDisabled, clickResult!.RejectionCode);
        Assert.True(clickResult.DryRun);

        var retiredPmsSurface = await SendAsync(
            client,
            PioneerRxActuationIpcCommands.WritebackRxDelivery,
            JsonSerializer.SerializeToElement(new { baaScopeTag = "DeliveryWriteback" }),
            shutdown.Token);
        Assert.Equal(IpcStatus.Ok, retiredPmsSurface.Status);
        var pmsResult = JsonSerializer.Deserialize<ActuationResult>(
            retiredPmsSurface.Data!.Value.GetRawText());
        Assert.NotNull(pmsResult);
        Assert.Equal(
            ActuationRejectionCodes.CapabilityUnavailable,
            pmsResult!.RejectionCode);
        Assert.False(pmsResult.DryRun);

        shutdown.Cancel();
    }

    private static async Task<IpcResponse> SendAsync(
        IpcPipeClient client,
        string command,
        JsonElement? data,
        CancellationToken cancellationToken)
    {
        var response = await client.SendAsync(
            new IpcRequest(Guid.NewGuid().ToString("N"), command, 1, data),
            cancellationToken);
        return Assert.IsType<IpcResponse>(response);
    }

    private sealed class MutableCapture : IScreenCapture
    {
        public bool IsAvailable { get; set; } = true;
        public bool Throw { get; set; }
        public ScreenBytes? Result { get; set; }
        public int CaptureCalls { get; private set; }

        public Task<ScreenBytes?> CapturePrimaryAsync(CancellationToken ct)
        {
            CaptureCalls++;
            if (Throw)
                throw new InvalidOperationException("capture failure detail");
            return Task.FromResult(Result);
        }
    }

    private sealed class RecordingStore : IScreenStore
    {
        public Task<string?> StoreAsync(ScreenBytes screen, CancellationToken ct) =>
            Task.FromResult<string?>("stored-screen");

        public Task<ScreenBytes?> LoadAsync(string id, CancellationToken ct) =>
            Task.FromResult<ScreenBytes?>(null);

        public Task<int> PurgeExpiredAsync(CancellationToken ct) => Task.FromResult(0);

        public Task<bool> DeleteAsync(string id, CancellationToken ct) =>
            Task.FromResult(true);
    }

    private sealed class MutableExtractor : IScreenExtractor
    {
        public string ExtractorId => "test-scrubbed";
        public bool IsReady => true;
        public ScreenFrame? Result { get; set; }

        public Task<ScreenFrame?> ExtractAsync(ScreenBytes screen, CancellationToken ct) =>
            Task.FromResult(Result);
    }
}
