using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Core.ActionGrammarV1.Workflows;
using SuavoAgent.Core.Autonomy;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Health;
using SuavoAgent.Core.Ipc;
using SuavoAgent.Core.Pricing;
using SuavoAgent.Core.State;
using SuavoAgent.Core.Vision;

namespace SuavoAgent.Core.Workers;

public sealed partial class HeartbeatWorker
{
    public HeartbeatWorker(
        ILogger<HeartbeatWorker> logger,
        IOptions<AgentOptions> options,
        IServiceProvider serviceProvider,
        AgentStateDb stateDb,
        WorkerHealthRegistry? healthRegistry = null)
        : base(logger, healthRegistry)
    {
        _logger = logger;
        _options = options.Value;
        _serviceProvider = serviceProvider;
        _stateDb = stateDb;
        _cloudClient = serviceProvider.GetService<SuavoCloudClient>();
        _contextAssembler = new Intelligence.ContextAssembler(stateDb);
        _efficiencyCalc = new Intelligence.EfficiencyCalculator(stateDb);
        _fleetChannels = new Intelligence.FleetDataChannels(stateDb);
        _pricingJobExecutor = serviceProvider.GetService<IPricingJobExecutor>();
        _pricingJobCloudUploader = serviceProvider.GetService<PricingJobCloudUploader>();
        _pricingTerminalAckOutbox = serviceProvider
            .GetService<PricingTerminalAckOutbox>();
        if (_pricingTerminalAckOutbox is null && _cloudClient is not null)
        {
            _pricingTerminalAckOutbox = new PricingTerminalAckOutbox(
                stateDb,
                _cloudClient,
                serviceProvider.GetService<ILogger<PricingTerminalAckOutbox>>()
                    ?? Microsoft.Extensions.Logging.Abstractions
                        .NullLogger<PricingTerminalAckOutbox>.Instance);
        }
        _taskAutonomy = serviceProvider.GetService<TaskAutonomyLedger>();
        _ipcCommandClient = serviceProvider.GetService<IpcCommandClient>();
        _intentCursorClient = serviceProvider.GetService<IIntentCursorClient>();
        _discoveryClient = serviceProvider.GetService<Discovery.DiscoveryClient>();
        _healthSignals = serviceProvider.GetService<IHealthSignals>();
        _healthCompositeCalculator = serviceProvider.GetService<HealthCompositeCalculator>();
        _cloudAuthRecovery = serviceProvider.GetService<CloudAuthRecoveryCoordinator>();
        _workflowExecutor = serviceProvider.GetService<WorkflowExecutor>();
        _actuationGateway = serviceProvider.GetService<
            ActionGrammarV1.Verbs.Actuation.IActuationGateway>();
        _localInference = serviceProvider.GetService<Reasoning.ILocalInference>();
        _actuationReadiness = serviceProvider.GetService<ActuationReadinessTracker>();
        _selfHealCoordinator = serviceProvider.GetService<HelperSelfHealCoordinator>();
        _activeLearnedRules = serviceProvider.GetService<Reasoning.IActiveLearnedRuleRegistry>();
        _visionConfigurationCoordinator = serviceProvider
            .GetService<VisionConfigurationCoordinator>();
        _visionConfigurationStatus = serviceProvider
            .GetService<VisionConfigurationStatusProvider>();
        if (_visionConfigurationCoordinator is not null &&
            _visionConfigurationStatus is not null &&
            _cloudClient is not null)
        {
            var visionDataDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "SuavoAgent");
            _visionConfigurationOutbox = new(
                stateDb,
                _visionConfigurationCoordinator,
                _visionConfigurationStatus,
                visionDataDirectory,
                effective => TesseractNativeCohortPolicy.VerifyInstalled(
                    effective.ToOptions().Tesseract),
                (commandId, succeeded, result, error, token) =>
                    _cloudClient.TryAckCommandAsync(
                        commandId,
                        succeeded,
                        result,
                        error,
                        token),
                _logger);
            var durableFailure = ((IVisionConfigurationCommandLedger)stateDb)
                .GetLatestVisionConfigurationStructuralFailure();
            if (durableFailure is { } failure)
                _visionConfigurationStatus.RecordStructuralFailure(
                    failure.Code,
                    failure.RecordedAt);
        }

        // Program registers this as a process-wide singleton so Companion
        // pause/stop reaches every active command.
        _autopilotRuns = serviceProvider.GetService<AutopilotRunCoordinator>()
            ?? new AutopilotRunCoordinator();
        var rxCorrelationStore = serviceProvider.GetService<IRxCorrelationStore>();
        if (_cloudClient is not null && rxCorrelationStore is not null)
        {
            _approvedPatientRetrieval = new ApprovedPatientRetrievalCoordinator(
                _options,
                rxCorrelationStore,
                stateDb,
                new PioneerRxApprovedPatientSource(serviceProvider),
                new SuavoApprovedPatientCloudTransport(_cloudClient),
                logger);
        }
        var deliveryLedger = serviceProvider.GetService<IDeliveryWritebackLedger>();
        if (_cloudClient is not null && rxCorrelationStore is not null && deliveryLedger is not null)
        {
            _deliveryWriteback = new DeliveryWritebackCoordinator(
                _options,
                rxCorrelationStore,
                deliveryLedger,
                stateDb,
                new PioneerRxDeliveryWritebackExecutor(serviceProvider),
                new SuavoDeliveryWritebackCloudTransport(_cloudClient),
                logger);
        }

        var agentId = _options.AgentId ?? "";
        var fingerprint = _options.MachineFingerprint ?? "";
        if (!string.IsNullOrEmpty(agentId))
        {
            _commandVerifier = new SignedCommandVerifier(
                new Dictionary<string, string>
                {
                    [RemoteCommandTrust.CommandV1KeyId] =
                        RemoteCommandTrust.CommandV1PublicKeyDer,
                },
                agentId,
                fingerprint);
        }
    }
}
