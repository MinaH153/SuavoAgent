using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Serilog;
using SuavoAgent.Core.Behavioral;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Health;
using SuavoAgent.Core.Ipc;
using SuavoAgent.Core.Learning;
using SuavoAgent.Core.Pricing;
using SuavoAgent.Core.Reasoning;
using SuavoAgent.Core.State;

namespace SuavoAgent.Core;

internal static class CoreRuntimeServiceRegistration
{
    public static void Register(
        HostApplicationBuilder builder,
        string commandPipeName,
        string eventPipeName,
        string pipeNonce)
    {
        // Pricing intelligence — Core→Helper command channel
        builder.Services.AddSingleton<IpcCommandClient>(sp =>
            new IpcCommandClient(
                commandPipeName,
                sp.GetRequiredService<ILogger<IpcCommandClient>>(),
                sp.GetRequiredService<Vision.VisionConfigurationStatusProvider>()
                    .EffectiveHandshake));
        builder.Services.AddSingleton<IIpcCommandClient>(sp => sp.GetRequiredService<IpcCommandClient>());
        builder.Services.AddSingleton<IIntentCursorClient, IntentCursorClient>();
        builder.Services.AddSingleton<ExcelPricingReader>();
        builder.Services.AddSingleton<ExcelPricingWriter>();
        builder.Services.AddSingleton<ExcelTop500Writer>();
        builder.Services.AddSingleton<PioneerRxTop500ProgressRelay>();
        // v2 retains the previously approved SQL-generated worklist semantics.
        builder.Services.AddSingleton<ITopDispensedWorklistBuilder,
            TopDispensedWorklistBuilder>();
        // v3 alone drives the fixed PioneerRx report/export workflow.
        builder.Services.AddSingleton<ITopDispensedWorklistProgressBuilder,
            PioneerRxExportTopDispensedWorklistBuilder>();
        builder.Services.AddSingleton<IPricedWorkbookPublisher,
            PioneerRxPricedWorkbookPublisher>();
        builder.Services.AddSingleton<IPricingLookupFactory, PioneerRxSqlPricingLookupFactory>();
        builder.Services.TryAddSingleton<SuavoAgent.Core.Autonomy.IPioneerRxAutonomyIdentityProvider>(sp =>
            new SuavoAgent.Core.Autonomy.PioneerRxAutonomyIdentityProvider(
                sp.GetRequiredService<IOptions<AgentOptions>>().Value));
        builder.Services.TryAddSingleton<ActuationReadinessTracker>();
        builder.Services.AddSingleton<PricingUiaActivityGate>();
        builder.Services.AddSingleton(sp =>
            new PackageCostApprovalBootstrapper(
                sp.GetRequiredService<IOptions<AgentOptions>>(),
                sp.GetRequiredService<AgentStateDb>(),
                sp.GetRequiredService<IIpcCommandClient>(),
                sp.GetRequiredService<PricingUiaActivityGate>(),
                sp.GetRequiredService<ActuationReadinessTracker>(),
                sp.GetRequiredService<SuavoAgent.Core.Autonomy.IPioneerRxAutonomyIdentityProvider>(),
                sp.GetRequiredService<ILogger<PackageCostApprovalBootstrapper>>()));
        builder.Services.AddHostedService(sp =>
            new SuavoAgent.Core.Workers.PackageCostApprovalBootstrapWorker(
                sp.GetRequiredService<PackageCostApprovalBootstrapper>(),
                sp.GetRequiredService<ILogger<
                    SuavoAgent.Core.Workers.PackageCostApprovalBootstrapWorker>>(),
                sp.GetService<SuavoAgent.Core.Workers.WorkerHealthRegistry>()));

        // Register both pricing executors as concrete singletons so either can be selected at
        // resolve time. The IPricingJobExecutor interface is bound below based on
        // AgentOptions.PricingExecutor — SqlFirst (default) or UiaFirst (Nadim-style UIA-only
        // pharmacies). Both implementations are fail-closed by design.
        builder.Services.AddSingleton<SqlFirstPricingJobExecutor>();
        builder.Services.AddSingleton<UiaFirstPricingJobExecutor>(sp =>
            new UiaFirstPricingJobExecutor(
                sp.GetRequiredService<PricingJobRunner>(),
                sp.GetRequiredService<IIpcCommandClient>(),
                sp.GetRequiredService<AgentStateDb>(),
                sp.GetRequiredService<SuavoAgent.Core.ActionGrammarV1.Verbs.Actuation.IActuationGateway>(),
                sp.GetRequiredService<ILogger<UiaFirstPricingJobExecutor>>(),
                sp.GetRequiredService<IOptions<AgentOptions>>(),
                sp.GetRequiredService<SuavoAgent.Core.Autonomy.IPioneerRxAutonomyIdentityProvider>(),
                SuavoAgent.Contracts.Maintenance.RemoteCommandTrust.CreateProductionKeyRegistry(),
                sp.GetRequiredService<PricingUiaActivityGate>()));
        builder.Services.AddSingleton<IPricingJobExecutor>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<AgentOptions>>().Value;
            IPricingJobExecutor executor = opts.PricingExecutor switch
            {
                // VisionFirst drives the screen identically to UiaFirst (same blind-run gate, same Helper
                // IPC path); the difference is the Helper reads the grid by sight when registry vision state is on.
                PricingExecutorMode.UiaFirst or PricingExecutorMode.VisionFirst
                    => sp.GetRequiredService<UiaFirstPricingJobExecutor>(),
                _ => sp.GetRequiredService<SqlFirstPricingJobExecutor>(),
            };
            Log.Information(
                "Pricing executor selected: {Mode} ({Type}); throttle={ThrottleMs}ms",
                opts.PricingExecutor, executor.GetType().Name, opts.PricingThrottleMs);
            return executor;
        });

        // Autonomous daily pricing schedule — the "bot does it on its own" overnight batch (Nadim's
        // documented top-500 run). OFF by default (Agent.PricingSchedule.Enabled=false). SqlFirst-ONLY by
        // construction: it is handed the read-only SqlFirstPricingJobExecutor and additionally self-gates on
        // PricingExecutor==SqlFirst, so a timer can never drive the live PMS UI (Precedence-1).
        builder.Services.AddHostedService(sp => new SuavoAgent.Core.Workers.PricingScheduleWorker(
            sp.GetRequiredService<ILogger<SuavoAgent.Core.Workers.PricingScheduleWorker>>(),
            sp.GetRequiredService<IOptionsMonitor<AgentOptions>>(),
            sp.GetRequiredService<SqlFirstPricingJobExecutor>(),
            // Optional: present only when an API key is configured (registered in the cloud block above).
            // When present, an autonomous run surfaces in the cockpit just like a cockpit-triggered one.
            sp.GetService<SuavoAgent.Core.Cloud.PricingJobCloudUploader>()));

        // File discovery — Core side. Helper runs the actual locator; this client
        // wraps the find_file IPC call so HeartbeatWorker can dispatch
        // find_and_run_pricing_job without knowing IPC details.
        builder.Services.AddSingleton<SuavoAgent.Core.Discovery.DiscoveryClient>();

        // SP4 Phase 5.2 — Actuation chain (Track 5). Verb registry + dispatcher +
        // workflow executor. The HelperActuationGateway wraps the existing IPC
        // command client so verbs delegate to the Helper-resident
        // SendInputDriver / UiaLabelResolver. Disabled-by-default by virtue of
        // the Helper-side ActuationGate (gate flips false unless operator
        // approves via cloud + signs the per-pharmacy actuation.json).
        builder.Services.AddSingleton<SuavoAgent.Core.ActionGrammarV1.Policy.IAuthzPolicy>(sp =>
            new SuavoAgent.Core.ActionGrammarV1.Policy.CharterDrivenAuthzPolicy());
        builder.Services.AddSingleton<SuavoAgent.Core.ActionGrammarV1.VerbDispatcher>();
        builder.Services.AddSingleton<SuavoAgent.Core.ActionGrammarV1.VerbRegistry>(sp =>
            SuavoAgent.Core.ActionGrammarV1.VerbRegistry.Build(
                new[] { typeof(SuavoAgent.Core.ActionGrammarV1.IVerb).Assembly },
                sp.GetRequiredService<ILogger<SuavoAgent.Core.ActionGrammarV1.VerbRegistry>>()));
        builder.Services.AddSingleton<SuavoAgent.Core.ActionGrammarV1.Verbs.Actuation.IActuationGateway>(sp =>
            new SuavoAgent.Core.ActionGrammarV1.Verbs.Actuation.HelperActuationGateway(
                clientFactory: () => new IpcCommandClient(
                    commandPipeName,
                    sp.GetRequiredService<ILogger<IpcCommandClient>>(),
                    sp.GetRequiredService<Vision.VisionConfigurationStatusProvider>()
                        .EffectiveHandshake),
                logger: sp.GetRequiredService<ILogger<SuavoAgent.Core.ActionGrammarV1.Verbs.Actuation.HelperActuationGateway>>()));
        builder.Services.AddSingleton<SuavoAgent.Core.ActionGrammarV1.Workflows.IWorkflowAuditClient>(sp =>
        {
            var cloud = sp.GetService<SuavoAgent.Core.Cloud.SuavoCloudClient>();
            if (cloud is null)
            {
                // Cloud is optional in some test/runtime configurations. Returning
                // a null-object lets workflows still execute locally for dry-run
                // smoke tests.
                return new SuavoAgent.Core.ActionGrammarV1.Workflows.NullWorkflowAuditClient();
            }
            return new SuavoAgent.Core.Cloud.WorkflowAuditCloudClient(
                cloud,
                sp.GetRequiredService<AgentStateDb>(),
                sp.GetRequiredService<IOptions<AgentOptions>>().Value,
                sp.GetRequiredService<ILogger<SuavoAgent.Core.Cloud.WorkflowAuditCloudClient>>());
        });
        builder.Services.AddHostedService(sp =>
            new SuavoAgent.Core.Workers.WorkflowAuditFlushWorker(
                sp.GetRequiredService<SuavoAgent.Core.ActionGrammarV1.Workflows.IWorkflowAuditClient>(),
                sp.GetRequiredService<ILogger<SuavoAgent.Core.Workers.WorkflowAuditFlushWorker>>(),
                sp.GetService<SuavoAgent.Core.Workers.WorkerHealthRegistry>()));
        builder.Services.AddSingleton<SuavoAgent.Core.ActionGrammarV1.Workflows.WorkflowExecutor>();

        // PricingJobRunner gets an optional TieredBrain evaluator wired only when
        // Reasoning.PricingBrainEnabled is true. Default: disabled — behavior is
        // byte-for-byte identical to pre-brain. Enabling lets the brain Halt jobs
        // on streak failures (Tier-1 rules) or ambiguous states (Tier-2/3).
        builder.Services.AddSingleton<PricingJobRunner>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<AgentOptions>>().Value;
            var reasoning = opts.Reasoning;
            PricingBrainEvaluator? evaluator = null;
            if (reasoning.PricingBrainEnabled)
            {
                evaluator = new PricingBrainEvaluator(
                    sp.GetRequiredService<TieredBrain>(),
                    sp.GetRequiredService<ILogger<PricingBrainEvaluator>>());
                Log.Information(
                    "Pricing brain ENABLED — PricingJobRunner will consult TieredBrain after each NDC lookup");
            }
            else
            {
                Log.Information(
                    "Pricing brain disabled (Reasoning.PricingBrainEnabled=false) — runner skips TieredBrain");
            }

            return new PricingJobRunner(
                sp.GetRequiredService<ExcelPricingReader>(),
                sp.GetRequiredService<ExcelPricingWriter>(),
                sp.GetRequiredService<AgentStateDb>(),
                sp.GetRequiredService<ILogger<PricingJobRunner>>(),
                evaluator,
                interLookupDelay: TimeSpan.FromMilliseconds(opts.PricingThrottleMs));
        });

        // Tier-1 Reasoning — rule engine. The bundled catalog is embedded in this
        // assembly so it travels inside the signed single-file exe; operator
        // overrides still load from ProgramData. Fail-closed: a malformed rule
        // file prevents the agent from starting.
        builder.Services.AddSingleton<YamlRuleLoader>();
        builder.Services.AddSingleton<ActiveLearnedRuleRegistry>(sp =>
        {
            var overrideDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "SuavoAgent", "rules");
            return new ActiveLearnedRuleRegistry(
                sp.GetRequiredService<AgentStateDb>(),
                overrideDir,
                sp.GetRequiredService<YamlRuleLoader>(),
                sp.GetRequiredService<ILogger<ActiveLearnedRuleRegistry>>());
        });
        builder.Services.AddSingleton<IActiveLearnedRuleRegistry>(sp =>
            sp.GetRequiredService<ActiveLearnedRuleRegistry>());
        builder.Services.AddSingleton<RuleEngine>(sp =>
        {
            var loader = sp.GetRequiredService<YamlRuleLoader>();
            var log = sp.GetRequiredService<ILogger<RuleEngine>>();

            var overrideDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "SuavoAgent", "rules");

            var rules = new List<SuavoAgent.Contracts.Reasoning.Rule>();
            rules.AddRange(loader.LoadFromEmbeddedResources(
                typeof(CoreRuntimeServiceRegistration).Assembly,
                "SuavoAgent.Core.Reasoning.Rules."));

            // Generated rules are never frozen into the startup catalog. Only the durable active registry
            // may expose them, which lets approval/demotion take effect immediately and prevents a stale
            // pre-007 Approved row from becoming executable. Hand-authored overrides remain immutable.
            var directoryRules = loader.LoadFromDirectory(overrideDir, required: false);
            rules.AddRange(directoryRules.Where(rule =>
                !rule.Id.StartsWith("auto.", StringComparison.Ordinal)));

            var engine = new RuleEngine(
                rules,
                log,
                sp.GetRequiredService<IActiveLearnedRuleRegistry>());
            Log.Information(
                "core.rule_engine.loaded count={Count} skills={Skills}",
                engine.RuleCount,
                engine.KnownSkills.Count);
            return engine;
        });

        // Tier-2 Reasoning — local inference. Selected at startup based on
        // ReasoningOptions + on-disk model verification. Default: NullLocalInference
        // so the agent boots useful in rules-only mode. Real LLM wiring lands in
        // Week 2c once a signed model manifest is shipped to GitHub releases.
        builder.Services.AddSingleton<ActionVerifier>(sp =>
            new ActionVerifier(sp.GetRequiredService<IOptions<AgentOptions>>()));
        // HttpModelProvisioner auto-downloads the GGUF on first run when ModelUrl is set (so the local
        // brain ships with a client install); with no URL it behaves exactly like the legacy verify-only
        // LocalFileModelManager. Fail-soft — a download failure just leaves reasoning off.
        builder.Services.AddSingleton<IModelManager, HttpModelProvisioner>();
        // NativeLibProvisioner downloads the llama.cpp native DLLs on first run when NativeLibsUrl is set
        // (they're deliberately not shipped — stealth). Background + fail-soft, like the model provisioner.
        builder.Services.AddSingleton<NativeLibProvisioner>();
        builder.Services.AddSingleton<ILocalInference>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<AgentOptions>>().Value.Reasoning;
            if (!opts.Enabled)
            {
                Log.Information("Tier-2 LocalInference disabled (Reasoning.Enabled=false) — running rules-only");
                return new NullLocalInference();
            }

            var publisher = opts.ValidatePublisherInstallation(
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "SuavoAgent"),
                DateTimeOffset.UtcNow);
            if (!publisher.IsValid)
            {
                Log.Error(
                    "Tier-2 LocalInference publisher authorization rejected ({Code}) — running rules-only",
                    publisher.Code);
                return new NullLocalInference();
            }

            // DeferredLocalInference kicks the one-time background provisioning of the model GGUF + native
            // llama.cpp DLLs and runs rules-only until they land, then lazily constructs the real engine on
            // the next call — no second restart, no startup stall (the old factory blocked here verifying a
            // 2 GB SHA-256). Native binaries stay off the installer (stealth); they're fetched on demand.
            Log.Information("core.local_inference.enabled");
            return new DeferredLocalInference(
                sp.GetRequiredService<IOptions<AgentOptions>>(),
                sp.GetRequiredService<NativeLibProvisioner>(),
                sp.GetRequiredService<IModelManager>(),
                sp.GetRequiredService<ILogger<LLamaLocalInference>>(),
                sp.GetRequiredService<ILogger<DeferredLocalInference>>());
        });

        // Tier-3 Reasoning — cloud Claude via /api/agent/reason. Opt-in via
        // Reasoning.CloudEnabled + the standard ApiKey (shared with heartbeat/sync).
        // No ApiKey means NullCloudReasoning, which TieredBrain treats as "skip Tier-3".
        builder.Services.AddSingleton<ICloudReasoning>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<AgentOptions>>().Value;
            if (!opts.Reasoning.CloudEnabled || string.IsNullOrWhiteSpace(opts.ApiKey))
            {
                Log.Information("Tier-3 CloudReasoning disabled (CloudEnabled={Enabled}, ApiKey={HasKey})",
                    opts.Reasoning.CloudEnabled, !string.IsNullOrWhiteSpace(opts.ApiKey));
                return new NullCloudReasoning();
            }

            var signer = sp.GetService<IPostSigner>();
            if (signer == null)
            {
                Log.Warning("Tier-3 CloudReasoning enabled but IPostSigner not registered — disabling");
                return new NullCloudReasoning();
            }

            Log.Information("Tier-3 CloudReasoning ENABLED — will escalate low-confidence Tier-2 proposals");
            return new ClaudeCloudReasoning(
                signer,
                opts,
                sp.GetRequiredService<ILogger<ClaudeCloudReasoning>>());
        });

        builder.Services.AddSingleton<TieredBrain>(sp =>
        {
            var reasoning = sp.GetRequiredService<IOptions<AgentOptions>>().Value.Reasoning;
            return new TieredBrain(
                sp.GetRequiredService<RuleEngine>(),
                sp.GetRequiredService<ILocalInference>(),
                sp.GetRequiredService<ActionVerifier>(),
                sp.GetRequiredService<ILogger<TieredBrain>>(),
                sp.GetService<ICloudReasoning>(),
                TimeSpan.FromSeconds(Math.Max(1, reasoning.InferenceTimeoutSeconds)));
        });

        builder.Services.AddSingleton<IpcPipeServer>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<IpcPipeServer>>();
            var eventRateLimiter = new SuavoAgent.Core.Ipc.EventRateLimiter(maxEventsPerSecond: 500);
            var helperAttestationPath = SuavoAgent.Contracts.Ipc.IpcPeerAttestationStore.GetDefaultPath();
            return new IpcPipeServer(eventPipeName, msg =>
            {
                logger.LogDebug("core.ipc.request_dispatched");

                switch (msg.Command)
                {
                    case SuavoAgent.Contracts.Ipc.IpcCommands.GetHealth:
                    {
                        var opts = sp.GetRequiredService<IOptions<AgentOptions>>().Value;
                        var db = sp.GetRequiredService<AgentStateDb>();
                        var snapshot = new HealthSnapshot(opts, db, sp, DateTimeOffset.UtcNow);
                        var data = snapshot.Take();
                        return Task.FromResult(new SuavoAgent.Contracts.Ipc.IpcResponse(
                            msg.Id, SuavoAgent.Contracts.Ipc.IpcStatus.Ok, msg.Command, data, null));
                    }

                    case SuavoAgent.Contracts.Ipc.IpcCommands.GetPharmacySalt:
                    {
                        // Legacy compatibility only. Batches produced from this
                        // daily key have no lease/session binding and are marked
                        // unverified by BehavioralEventReceiver.
                        var opts = sp.GetRequiredService<IOptions<AgentOptions>>().Value;
                        var db = sp.GetRequiredService<AgentStateDb>();
                        var sessionId = db.GetActiveSessionId(opts.PharmacyId ?? "");
                        var masterSalt = sessionId != null
                            ? db.GetOrCreateHmacSalt(sessionId)
                            : db.GetOrCreateObservationHmacSalt();
                        // C-1: derive date-scoped ephemeral key — master salt never crosses the IPC boundary.
                        // Leaking the derived key can't de-anonymize data from other days.
                        string ephemeralKey = "";
                        if (masterSalt.Length > 0)
                        {
                            var dayBytes = System.Text.Encoding.UTF8.GetBytes(
                                DateTimeOffset.UtcNow.ToString("yyyy-MM-dd"));
                            ephemeralKey = Convert.ToBase64String(
                                System.Security.Cryptography.HMACSHA256.HashData(
                                    System.Text.Encoding.UTF8.GetBytes(masterSalt), dayBytes));
                        }
                        var saltJson = System.Text.Json.JsonSerializer.SerializeToElement(ephemeralKey);
                        return Task.FromResult(new SuavoAgent.Contracts.Ipc.IpcResponse(
                            msg.Id, SuavoAgent.Contracts.Ipc.IpcStatus.Ok, msg.Command, saltJson, null));
                    }

                    case SuavoAgent.Contracts.Ipc.IpcCommands.GetObservationLease:
                    {
                        var opts = sp.GetRequiredService<IOptions<AgentOptions>>().Value;
                        var db = sp.GetRequiredService<AgentStateDb>();
                        var sessionId = db.GetActiveSessionId(opts.PharmacyId ?? string.Empty);
                        string? currentLeaseId = null;
                        if (msg.Data is { ValueKind: System.Text.Json.JsonValueKind.Object } leaseRequestData)
                        {
                            currentLeaseId = System.Text.Json.JsonSerializer
                                .Deserialize<SuavoAgent.Contracts.Behavioral.ObservationKeyLeaseRequest>(
                                    leaseRequestData.GetRawText())
                                ?.CurrentLeaseId;
                        }
                        var lease = db.GetOrIssueObservationKeyLease(
                            sessionId,
                            currentLeaseId,
                            DateTimeOffset.UtcNow,
                            TimeSpan.FromMinutes(15),
                            minimumRemaining: TimeSpan.FromMinutes(3));
                        return Task.FromResult(new SuavoAgent.Contracts.Ipc.IpcResponse(
                            msg.Id,
                            SuavoAgent.Contracts.Ipc.IpcStatus.Ok,
                            msg.Command,
                            System.Text.Json.JsonSerializer.SerializeToElement(lease),
                            null));
                    }

                    case SuavoAgent.Contracts.Ipc.IpcCommands.GetAutopilotControlState:
                    {
                        var state = sp.GetRequiredService<SuavoAgent.Core.Autonomy.AutopilotRunCoordinator>()
                            .Snapshot();
                        return Task.FromResult(new SuavoAgent.Contracts.Ipc.IpcResponse(
                            msg.Id,
                            SuavoAgent.Contracts.Ipc.IpcStatus.Ok,
                            msg.Command,
                            System.Text.Json.JsonSerializer.SerializeToElement(state),
                            null));
                    }

                    case SuavoAgent.Contracts.Ipc.IpcCommands.AutopilotControl:
                    {
                        SuavoAgent.Contracts.Ipc.AutopilotControlRequest? request = null;
                        if (msg.Data is { ValueKind: System.Text.Json.JsonValueKind.Object } controlData)
                        {
                            try
                            {
                                request = System.Text.Json.JsonSerializer.Deserialize<
                                    SuavoAgent.Contracts.Ipc.AutopilotControlRequest>(
                                    controlData.GetRawText());
                            }
                            catch (System.Text.Json.JsonException) { }
                        }

                        var valid = request is not null
                            && request.ContractVersion
                                == SuavoAgent.Contracts.Ipc.AutopilotControlRequest.CurrentContractVersion
                            && string.Equals(request.ReasonCode, "companion_control", StringComparison.Ordinal)
                            && request.Action is "pause" or "resume" or "stop"
                            && (request.Action == "resume"
                                ? request.ExpectedControlGeneration.HasValue
                                : !request.ExpectedControlGeneration.HasValue);
                        if (!valid)
                        {
                            return Task.FromResult(new SuavoAgent.Contracts.Ipc.IpcResponse(
                                msg.Id,
                                SuavoAgent.Contracts.Ipc.IpcStatus.BadRequest,
                                msg.Command,
                                null,
                                new SuavoAgent.Contracts.Ipc.IpcError(
                                    "autopilot_control_invalid",
                                    "The local Autopilot control request was invalid.",
                                    false,
                                    0)));
                        }

                        var action = request!.Action switch
                        {
                            "pause" => SuavoAgent.Core.Autonomy.AutopilotControlAction.Pause,
                            "resume" => SuavoAgent.Core.Autonomy.AutopilotControlAction.Resume,
                            _ => SuavoAgent.Core.Autonomy.AutopilotControlAction.Stop,
                        };
                        var receipt = sp
                            .GetRequiredService<SuavoAgent.Core.Autonomy.AutopilotRunCoordinator>()
                            .ApplyLocalControl(
                                action,
                                request.ReasonCode,
                                request.ExpectedControlGeneration);
                        return Task.FromResult(new SuavoAgent.Contracts.Ipc.IpcResponse(
                            msg.Id,
                            SuavoAgent.Contracts.Ipc.IpcStatus.Ok,
                            msg.Command,
                            System.Text.Json.JsonSerializer.SerializeToElement(receipt),
                            null));
                    }

                    case SuavoAgent.Contracts.Ipc.IpcCommands.HelperStatus:
                    case SuavoAgent.Contracts.Ipc.IpcCommands.HelperError:
                    {
                        if (msg.Data is { ValueKind: System.Text.Json.JsonValueKind.Object } statusData
                            && statusData.TryGetProperty("code", out var codeElement)
                            && codeElement.ValueKind == System.Text.Json.JsonValueKind.String)
                        {
                            var code = codeElement.GetString();
                            if (!string.IsNullOrWhiteSpace(code)
                                && code.Length <= 96
                                && code.All(character =>
                                    char.IsAsciiLetterOrDigit(character) || character is '_' or '-')
                                && (string.Equals(
                                        code,
                                        "observation_spool_healthy",
                                        StringComparison.Ordinal)
                                    || code.StartsWith("observation_", StringComparison.Ordinal)))
                            {
                                sp.GetRequiredService<AgentStateDb>()
                                    .RecordObservationSpoolStatus(code);
                            }
                        }
                        return Task.FromResult(new SuavoAgent.Contracts.Ipc.IpcResponse(
                            msg.Id,
                            SuavoAgent.Contracts.Ipc.IpcStatus.Ok,
                            msg.Command,
                            null,
                            null));
                    }

                    case SuavoAgent.Contracts.Ipc.IpcCommands.BehavioralEvents:
                    {
                        var response = SuavoAgent.Core.Ipc.BehavioralBatchIpcProcessor.Process(
                            msg,
                            SuavoAgent.Contracts.Behavioral.BehavioralEventChannels.Pms,
                            sp.GetRequiredService<BehavioralEventReceiver>(),
                            eventRateLimiter);
                        return Task.FromResult(response);
                    }

                    case SuavoAgent.Contracts.Ipc.IpcCommands.SystemEvents:
                    {
                        var response = SuavoAgent.Core.Ipc.BehavioralBatchIpcProcessor.Process(
                            msg,
                            SuavoAgent.Contracts.Behavioral.BehavioralEventChannels.System,
                            sp.GetRequiredService<BehavioralEventReceiver>(),
                            eventRateLimiter);
                        return Task.FromResult(response);
                    }

                    case SuavoAgent.Contracts.Ipc.IpcCommands.PricingJobProgress:
                        return SuavoAgent.Core.Pricing.PioneerRxTop500ProgressIpcProcessor
                            .ProcessAsync(
                                msg,
                                sp.GetRequiredService<
                                    SuavoAgent.Core.Pricing.PioneerRxTop500ProgressRelay>());

                    default:
                        return Task.FromResult(new SuavoAgent.Contracts.Ipc.IpcResponse(
                            msg.Id, SuavoAgent.Contracts.Ipc.IpcStatus.Ok, msg.Command, null, null));
                }
            }, logger, isBrokerAttestedHelper: evidence =>
                SuavoAgent.Contracts.Ipc.IpcPeerAttestationStore.ContainsHelper(
                    helperAttestationPath,
                    pipeNonce,
                    evidence.ProcessId,
                    evidence.SessionId,
                    evidence.ProcessStartedAtUtc,
                    evidence.CurrentHelperSha256,
                    DateTimeOffset.UtcNow,
                    TimeSpan.FromMinutes(5)));
        });
    }
}
