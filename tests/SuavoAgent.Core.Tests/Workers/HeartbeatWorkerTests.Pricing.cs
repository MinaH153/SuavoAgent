using System.Reflection;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Core.Behavioral;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Autonomy;
using SuavoAgent.Core.Ipc;
using SuavoAgent.Core.Learning;
using SuavoAgent.Core.Pricing;
using SuavoAgent.Core.State;
using SuavoAgent.Core.Tests.Pricing;
using SuavoAgent.Core.Workers;
using Xunit;

namespace SuavoAgent.Core.Tests.Workers;

public partial class HeartbeatWorkerTests
{
    [Fact]
    public async Task GeneratedPricingV2_BuildsLocalWorklist_ThenUsesSignedExecutor()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.xlsx");
        await File.WriteAllTextAsync(path, "local aggregate worklist");
        try
        {
            const string commandId = "30000000-0000-4000-8000-000000000099";
            _worklistBuilder.Result = TopDispensedWorklistBuildResult.Success(
                path,
                37);
            var response = BuildAuthorizedGeneratedPricingResponseJson(
                _db,
                commandId);

            await InvokeProcessAsync(response);
            await _pricingJobExecutor.Started.Task.WaitAsync(
                TimeSpan.FromSeconds(5));

            Assert.Equal(commandId, Assert.Single(_worklistBuilder.CommandIds));
            var spec = Assert.Single(_pricingJobExecutor.Specs);
            Assert.Equal(path, spec.ExcelPath);
            Assert.NotNull(spec.ApprovalId);
            Assert.NotNull(spec.GrantDigest);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    // ── Command Dispatch: run_pricing_job ──

    [Theory]
    [InlineData(null, null)]
    [InlineData("11111111-1111-4111-8111-111111111111", null)]
    [InlineData(null, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("11111111-1111-1111-8111-111111111111", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("11111111-1111-4111-8111-111111111111", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task Pricing_dispatch_InvalidAuthorityBinding_NeverConsumesNonceOrRuns(
        string? approvalId,
        string? grantDigest)
    {
        var data = new JsonObject
        {
            ["pricingCandidateToken"] = "pdc_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            ["commandId"] = "30000000-0000-4000-8000-000000000011",
        };
        if (approvalId is not null)
            data["approvalId"] = approvalId;
        if (grantDigest is not null)
            data["grantDigest"] = grantDigest;
        var response = BuildResponseJson("run_pricing_job", data);
        var signed = response.GetProperty("data").GetProperty("signedCommand");
        var nonce = signed.GetProperty("nonce").GetString()!;

        await InvokeProcessAsync(response);

        Assert.Empty(_pricingJobExecutor.Specs);
        Assert.True(_db.TryRecordNonce(nonce));
    }

    [Fact]
    public async Task Pricing_dispatch_MalformedCommandId_NeverConsumesAuthorityOrRuns()
    {
        var response = BuildAuthorizedPricingResponseJson(_db, new
        {
            pricingCandidateToken = "pdc_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            commandId = "malformed-command",
        });
        var signed = response.GetProperty("data").GetProperty("signedCommand");
        var nonce = signed.GetProperty("nonce").GetString()!;

        await InvokeProcessAsync(response);

        Assert.Empty(_pricingJobExecutor.Specs);
        Assert.True(_db.TryRecordNonce(nonce));
    }

    [Fact]
    public async Task Pricing_dispatch_MissingDurableOutbox_FailsBeforeNonceOrExecution()
    {
        using var db = new AgentStateDb(":memory:");
        var executor = new FakePricingJobExecutor();
        var services = new ServiceCollection()
            .AddSingleton(db)
            .AddSingleton<IPricingJobExecutor>(executor)
            .AddSingleton(new AutopilotRunCoordinator())
            .AddSingleton(_observationAuthority)
            .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .BuildServiceProvider();
        var options = Options.Create(new AgentOptions
        {
            AgentId = TestAgentId,
            MachineFingerprint = TestFingerprint,
            PharmacyId = TestPharmacyId,
            PricingExecutor = PricingExecutorMode.SqlFirst,
        });
        var worker = new HeartbeatWorker(
            NullLogger<HeartbeatWorker>.Instance,
            options,
            services,
            db);
        var verifierField = typeof(HeartbeatWorker)
            .GetField("_commandVerifier", BindingFlags.NonPublic | BindingFlags.Instance)!;
        verifierField.SetValue(worker, new SignedCommandVerifier(
            new Dictionary<string, string> { [TestKeyId] = _pubKeyDer },
            TestAgentId,
            TestFingerprint));
        var response = BuildAuthorizedPricingResponseJson(db, new
        {
            pricingCandidateToken = "pdc_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            commandId = "30000000-0000-4000-8000-00000000000b",
        });
        var signed = response.GetProperty("data").GetProperty("signedCommand");
        var nonce = signed.GetProperty("nonce").GetString()!;

        var invocation = (Task)_processMethod.Invoke(
            worker,
            new object[] { response, CancellationToken.None })!;
        await invocation;

        Assert.Empty(executor.Specs);
        Assert.True(db.TryRecordNonce(nonce));
        services.Dispose();
    }

    [Fact]
    public async Task Pricing_dispatch_AuthorityExpiresAfterIntent_StagesTerminalFailure()
    {
        using var db = new AgentStateDb(":memory:");
        var executor = new FakePricingJobExecutor();
        var acked = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        string? sentError = null;
        object? sentResult = new();
        var outbox = new PricingTerminalAckOutbox(
            db,
            (_, succeeded, result, error, _) =>
            {
                Assert.False(succeeded);
                sentResult = result;
                sentError = error;
                acked.TrySetResult(true);
                return Task.FromResult(true);
            },
            NullLogger<PricingTerminalAckOutbox>.Instance);
        var services = new ServiceCollection()
            .AddSingleton(db)
            .AddSingleton<IPricingJobExecutor>(executor)
            .AddSingleton(new AutopilotRunCoordinator())
            .AddSingleton(outbox)
            .AddSingleton(_observationAuthority)
            .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .BuildServiceProvider();
        var options = Options.Create(new AgentOptions
        {
            AgentId = TestAgentId,
            MachineFingerprint = TestFingerprint,
            PharmacyId = TestPharmacyId,
            PricingExecutor = PricingExecutorMode.SqlFirst,
        });
        var worker = new HeartbeatWorker(
            NullLogger<HeartbeatWorker>.Instance,
            options,
            services,
            db);
        var now = DateTimeOffset.UtcNow;
        var verifierField = typeof(HeartbeatWorker)
            .GetField("_commandVerifier", BindingFlags.NonPublic | BindingFlags.Instance)!;
        verifierField.SetValue(worker, new SignedCommandVerifier(
            new Dictionary<string, string> { [TestKeyId] = _pubKeyDer },
            TestAgentId,
            TestFingerprint,
            new ExpireAtDetachedDispatchTimeProvider(now)));
        var token = db.SavePricingDiscoveryCandidate(
            Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.xlsx"));
        const string commandId = "30000000-0000-4000-8000-00000000000d";
        var response = BuildAuthorizedPricingResponseJson(db, new
        {
            pricingCandidateToken = token,
            commandId,
            expiresAt = now.AddSeconds(1).ToString("O"),
        });

        var invocation = (Task)_processMethod.Invoke(
            worker,
            new object[] { response, CancellationToken.None })!;
        await invocation;
        await acked.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(executor.Specs);
        Assert.Null(sentResult);
        Assert.Equal("pricing_execution_exception", sentError);
        Assert.Equal("delivered", db.GetPricingTerminalAck(commandId)!.State);
        services.Dispose();
    }

    [Fact]
    public async Task RunPricingJob_UsesSqlFirstPricingExecutor()
    {
        var xlsx = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.xlsx");
        await File.WriteAllTextAsync(xlsx, "placeholder");
        try
        {
            var token = _db.SavePricingDiscoveryCandidate(xlsx);
            var response = BuildAuthorizedPricingResponseJson(_db, new
            {
                pricingCandidateToken = token,
                ndcColumn = "NDC",
                supplierColumn = "Supplier",
                costColumn = "Cost (per unit)",
                commandId = "30000000-0000-4000-8000-000000000001"
            });
            var sc = response.GetProperty("data").GetProperty("signedCommand");

            await InvokeRunPricingAsync(sc);

            var spec = Assert.Single(_pricingJobExecutor.Specs);
            Assert.Equal(xlsx, spec.ExcelPath);
            Assert.Equal(PricingJobDefaults.NdcColumn, spec.NdcColumn);
            Assert.Equal(PricingJobDefaults.SupplierColumn, spec.SupplierColumn);
            Assert.Equal(PricingJobDefaults.CostColumn, spec.CostColumn);
        }
        finally
        {
            try { File.Delete(xlsx); } catch { }
        }
    }

    [Fact]
    public async Task RunPricingJob_DefaultsToNadimFriendlyOutputColumns()
    {
        var xlsx = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.xlsx");
        await File.WriteAllTextAsync(xlsx, "placeholder");
        try
        {
            var token = _db.SavePricingDiscoveryCandidate(xlsx);
            var response = BuildAuthorizedPricingResponseJson(_db, new
            {
                pricingCandidateToken = token,
                commandId = "30000000-0000-4000-8000-000000000002"
            });
            var sc = response.GetProperty("data").GetProperty("signedCommand");

            await InvokeRunPricingAsync(sc);

            var spec = Assert.Single(_pricingJobExecutor.Specs);
            Assert.Equal(PricingJobDefaults.NdcColumn, spec.NdcColumn);
            Assert.Equal(PricingJobDefaults.SupplierColumn, spec.SupplierColumn);
            Assert.Equal(PricingJobDefaults.CostColumn, spec.CostColumn);
        }
        finally
        {
            try { File.Delete(xlsx); } catch { }
        }
    }

    [Fact]
    public async Task RunPricingJob_ResolvesOpaqueDiscoveryCandidateTokenLocally()
    {
        var xlsx = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.xlsx");
        await File.WriteAllTextAsync(xlsx, "placeholder");
        try
        {
            var token = _db.SavePricingDiscoveryCandidate(xlsx);
            var response = BuildAuthorizedPricingResponseJson(_db, new
            {
                pricingCandidateToken = token,
                commandId = "30000000-0000-4000-8000-000000000003"
            });
            var sc = response.GetProperty("data").GetProperty("signedCommand");

            await InvokeRunPricingAsync(sc);

            var spec = Assert.Single(_pricingJobExecutor.Specs);
            Assert.Equal(xlsx, spec.ExcelPath);
        }
        finally
        {
            try { File.Delete(xlsx); } catch { }
        }
    }

    [Fact]
    public async Task RunPricingJob_RejectsUnknownDiscoveryCandidateToken()
    {
        var response = BuildAuthorizedPricingResponseJson(_db, new
        {
            pricingCandidateToken = "pdc_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            commandId = "30000000-0000-4000-8000-000000000004"
        });
        var sc = response.GetProperty("data").GetProperty("signedCommand");

        await InvokeRunPricingAsync(sc);

        Assert.Empty(_pricingJobExecutor.Specs);
    }

    [Fact]
    public async Task RunPricingJob_PausedAutopilot_RejectsBeforeExecutorAdmission()
    {
        var xlsx = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.xlsx");
        var token = _db.SavePricingDiscoveryCandidate(xlsx);
        _autopilotRuns.ApplyControl(AutopilotControlAction.Pause, "test_pause");
        var response = BuildAuthorizedPricingResponseJson(_db, new
        {
            pricingCandidateToken = token,
            commandId = "30000000-0000-4000-8000-000000000005"
        });
        var sc = response.GetProperty("data").GetProperty("signedCommand");

        await InvokeRunPricingAsync(sc);

        Assert.Empty(_pricingJobExecutor.Specs);
        Assert.Equal(0, _autopilotRuns.Snapshot().ActiveRunCount);
    }

    [Fact]
    public async Task RunPricingJob_PausedAutopilot_AcksOnlyStructuralRejection()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"suavo_hb_ack_{Guid.NewGuid():N}.db");
        using var db = new AgentStateDb(dbPath);
        var handler = new RecordingAckHandler();
        var options = new AgentOptions
        {
            AgentId = TestAgentId,
            MachineFingerprint = TestFingerprint,
            PharmacyId = TestPharmacyId,
            ApiKey = "unit-test-key",
            CloudUrl = "https://suavo.test",
            PricingExecutor = PricingExecutorMode.SqlFirst,
        };
        using var cloud = new SuavoCloudClient(options, handler);
        var coordinator = new AutopilotRunCoordinator();
        coordinator.ApplyControl(AutopilotControlAction.Pause, "test_pause");
        var executor = new FakePricingJobExecutor();
        var outbox = new PricingTerminalAckOutbox(
            db,
            cloud,
            NullLogger<PricingTerminalAckOutbox>.Instance);
        var services = new ServiceCollection()
            .AddSingleton(db)
            .AddSingleton(cloud)
            .AddSingleton(coordinator)
            .AddSingleton<IPricingJobExecutor>(executor)
            .AddSingleton(outbox)
            .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .BuildServiceProvider();
        var worker = new HeartbeatWorker(
            NullLogger<HeartbeatWorker>.Instance,
            Options.Create(options),
            services,
            db);
        var token = db.SavePricingDiscoveryCandidate(
            Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.xlsx"));
        var response = BuildAuthorizedPricingResponseJson(db, new
        {
            pricingCandidateToken = token,
            commandId = "30000000-0000-4000-8000-000000000006"
        });
        var sc = response.GetProperty("data").GetProperty("signedCommand");
        RegisterPricingCommandForDirectInvocation(outbox, sc);

        var invocation = (Task)_runPricingMethod.Invoke(
            worker,
            new object[] { sc, CancellationToken.None })!;
        await invocation;

        Assert.Empty(executor.Specs);
        Assert.Equal(
            "/api/agent/commands/30000000-0000-4000-8000-000000000006/ack",
            handler.Path);
        using var body = JsonDocument.Parse(Assert.IsType<string>(handler.Body));
        Assert.Equal("failed", body.RootElement.GetProperty("status").GetString());
        Assert.Equal("autopilot_paused", body.RootElement.GetProperty("error").GetString());
        var result = body.RootElement.GetProperty("result");
        Assert.False(result.GetProperty("admitted").GetBoolean());
        Assert.Equal("Pricing", result.GetProperty("kind").GetString());
        Assert.Equal("autopilot_paused", result.GetProperty("outcome").GetString());

        services.Dispose();
        try { File.Delete(dbPath); } catch { }
    }

    [Fact]
    public async Task RunPricingJob_LocalAutopilotPause_CancelsLinkedExecutorAndReleasesLease()
    {
        var xlsx = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.xlsx");
        var token = _db.SavePricingDiscoveryCandidate(xlsx);
        _pricingJobExecutor.BlockUntilCancellation = true;
        var response = BuildAuthorizedPricingResponseJson(_db, new
        {
            pricingCandidateToken = token,
            commandId = "30000000-0000-4000-8000-000000000007"
        });
        var sc = response.GetProperty("data").GetProperty("signedCommand");

        var runTask = InvokeRunPricingAsync(sc);
        await _pricingJobExecutor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var active = _autopilotRuns.Snapshot();
        Assert.Equal(1, active.ActiveRunCount);
        Assert.Equal(new[] { AutopilotRunKind.Pricing }, active.ActiveKinds);

        _autopilotRuns.ApplyControl(AutopilotControlAction.Pause, "test_pause");
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(_pricingJobExecutor.CancellationObserved);
        Assert.Equal(0, _autopilotRuns.Snapshot().ActiveRunCount);
    }

    [Fact]
    public async Task RunPricingJob_LocalPause_UsesOriginalCommandTokenForCancellationAck()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"suavo_hb_cancel_ack_{Guid.NewGuid():N}.db");
        using var db = new AgentStateDb(dbPath);
        var handler = new RecordingAckHandler();
        var options = new AgentOptions
        {
            AgentId = TestAgentId,
            MachineFingerprint = TestFingerprint,
            PharmacyId = TestPharmacyId,
            ApiKey = "unit-test-key",
            CloudUrl = "https://suavo.test",
            PricingExecutor = PricingExecutorMode.SqlFirst,
        };
        using var cloud = new SuavoCloudClient(options, handler);
        var coordinator = new AutopilotRunCoordinator();
        var executor = new FakePricingJobExecutor { BlockUntilCancellation = true };
        var outbox = new PricingTerminalAckOutbox(
            db,
            cloud,
            NullLogger<PricingTerminalAckOutbox>.Instance);
        var services = new ServiceCollection()
            .AddSingleton(db)
            .AddSingleton(cloud)
            .AddSingleton(coordinator)
            .AddSingleton<IPricingJobExecutor>(executor)
            .AddSingleton(outbox)
            .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .BuildServiceProvider();
        var worker = new HeartbeatWorker(
            NullLogger<HeartbeatWorker>.Instance,
            Options.Create(options),
            services,
            db);
        var token = db.SavePricingDiscoveryCandidate(
            Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.xlsx"));
        var response = BuildAuthorizedPricingResponseJson(db, new
        {
            pricingCandidateToken = token,
            commandId = "30000000-0000-4000-8000-000000000008"
        });
        var sc = response.GetProperty("data").GetProperty("signedCommand");
        RegisterPricingCommandForDirectInvocation(outbox, sc);

        var runTask = (Task)_runPricingMethod.Invoke(
            worker,
            new object[] { sc, CancellationToken.None })!;
        await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        coordinator.ApplyControl(AutopilotControlAction.Pause, "test_pause");
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(executor.CancellationObserved);
        Assert.Equal(
            "/api/agent/commands/30000000-0000-4000-8000-000000000008/ack",
            handler.Path);
        using var body = JsonDocument.Parse(Assert.IsType<string>(handler.Body));
        Assert.Equal("pricing_cancelled", body.RootElement.GetProperty("error").GetString());
        Assert.Equal(
            "cancelled",
            body.RootElement.GetProperty("result").GetProperty("status").GetString());

        services.Dispose();
        try { File.Delete(dbPath); } catch { }
    }

    [Fact]
    public async Task RunPricingJob_LocalPause_DuringResultUpload_PreservesDurableResult()
    {
        var dbPath = Path.Combine(
            Path.GetTempPath(), $"suavo_hb_result_cancel_{Guid.NewGuid():N}.db");
        using var db = new AgentStateDb(dbPath);
        var coordinator = new AutopilotRunCoordinator();
        var observation = PricingTestAuthority.Contract();
        var authority = PricingTestAuthority.InstallAuthority(
            db,
            observation,
            pharmacyId: TestPharmacyId,
            agentId: TestAgentId,
            machineFingerprint: TestFingerprint);
        var executor = new FakePricingJobExecutor
        {
            PersistCompletedResultTo = db,
            BeforePersistCompletedResult = spec => Assert.True(
                db.TryBindPricingInputIdentity(
                    spec.JobId,
                    new string('a', 64),
                    new string('b', 64),
                    observation,
                    authority,
                    DateTimeOffset.UtcNow,
                    out var code),
                code),
        };
        var signer = new CancellationBlockingPostSigner();
        var uploader = new PricingJobCloudUploader(
            signer,
            db,
            NullLogger<PricingJobCloudUploader>.Instance,
            PricingTestAuthority.TrustedPublicKeys);
        var ackAttempts = 0;
        var outbox = new PricingTerminalAckOutbox(
            db,
            (_, _, _, _, _) =>
            {
                ackAttempts++;
                return Task.FromResult(true);
            },
            NullLogger<PricingTerminalAckOutbox>.Instance,
            new Dictionary<string, string> { [TestKeyId] = _pubKeyDer });
        const string commandId = "30000000-0000-4000-8000-00000000000c";
        var options = new AgentOptions
        {
            AgentId = TestAgentId,
            MachineFingerprint = TestFingerprint,
            PharmacyId = TestPharmacyId,
            PricingExecutor = PricingExecutorMode.SqlFirst,
        };
        var services = new ServiceCollection()
            .AddSingleton(db)
            .AddSingleton(coordinator)
            .AddSingleton<IPricingJobExecutor>(executor)
            .AddSingleton(uploader)
            .AddSingleton(outbox)
            .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .BuildServiceProvider();
        var worker = new HeartbeatWorker(
            NullLogger<HeartbeatWorker>.Instance,
            Options.Create(options),
            services,
            db);
        var token = db.SavePricingDiscoveryCandidate(
            Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.xlsx"));
        var response = BuildAuthorizedPricingResponseJson(db, new
        {
            pricingCandidateToken = token,
            commandId,
        }, authority);
        var signed = response.GetProperty("data").GetProperty("signedCommand");
        RegisterPricingCommandForDirectInvocation(outbox, signed);

        var runTask = (Task)_runPricingMethod.Invoke(
            worker,
            new object[] { signed, CancellationToken.None })!;
        await signer.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        coordinator.ApplyControl(AutopilotControlAction.Pause, "test_pause");
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(signer.CancellationObserved);
        Assert.Equal(0, ackAttempts);
        Assert.Null(db.GetPricingTerminalAck(commandId));
        Assert.Equal("pending", Assert.IsType<AgentStateDb.PricingResultOutboxEntry>(
            db.GetPricingResultOutbox(executor.Specs.Single().JobId)).State);

        services.Dispose();
        try { File.Delete(dbPath); } catch { }
    }

    [Fact]
    public async Task RunPricingJob_HostCancellation_StagesDurableFailureBeforeRethrow()
    {
        var token = _db.SavePricingDiscoveryCandidate(
            Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.xlsx"));
        _pricingJobExecutor.BlockUntilCancellation = true;
        const string commandId = "30000000-0000-4000-8000-00000000000a";
        var response = BuildAuthorizedPricingResponseJson(_db, new
        {
            pricingCandidateToken = token,
            commandId,
        });
        var sc = response.GetProperty("data").GetProperty("signedCommand");
        RegisterPricingCommandForDirectInvocation(
            _pricingTerminalAckOutbox,
            sc);
        using var hostCancellation = new CancellationTokenSource();

        var runTask = (Task)_runPricingMethod.Invoke(
            _worker,
            new object[] { sc, hostCancellation.Token })!;
        await _pricingJobExecutor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        hostCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runTask);

        var staged = _db.GetPricingTerminalAck(commandId);
        Assert.NotNull(staged);
        Assert.Equal("pending", staged!.State);
        Assert.Equal("cancelled", staged.Ack.ResultKind);
        Assert.Equal("pricing_cancelled", staged.Ack.ErrorCode);
    }

    [Theory]
    [InlineData("cancel", "cancelled")]
    [InlineData("exception", "failed")]
    [InlineData("result_sync", "failed")]
    public async Task RunPricingJob_EveryAdmittedFailure_RecordsOneNegativeTerminalReceipt(
        string scenario,
        string expectedResult)
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"suavo_hb_terminal_{Guid.NewGuid():N}.db");
        using var db = new AgentStateDb(dbPath);
        var options = new AgentOptions
        {
            AgentId = "11111111-1111-4111-8111-111111111111",
            PharmacyId = "22222222-2222-4222-8222-222222222222",
            MachineFingerprint = "terminal-evidence-box",
            PricingExecutor = PricingExecutorMode.SqlFirst,
        };
        using var signer = new TestAutonomyDeviceSigner();
        var ledger = new TaskAutonomyLedger(db, 12, options, signer);
        var coordinator = new AutopilotRunCoordinator();
        var executor = new FakePricingJobExecutor
        {
            BlockUntilCancellation = scenario == "cancel",
            Failure = scenario == "exception"
                ? new InvalidOperationException("test_failure")
                : null,
        };
        var outbox = new PricingTerminalAckOutbox(
            db,
            (_, _, _, _, _) => Task.FromResult(true),
            NullLogger<PricingTerminalAckOutbox>.Instance,
            new Dictionary<string, string> { [TestKeyId] = _pubKeyDer });
        var services = new ServiceCollection()
            .AddSingleton(db)
            .AddSingleton(coordinator)
            .AddSingleton<IPricingJobExecutor>(executor)
            .AddSingleton(ledger)
            .AddSingleton(outbox)
            .AddSingleton<IPioneerRxAutonomyIdentityProvider>(
                new TestPioneerRxAutonomyIdentityProvider())
            .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .BuildServiceProvider();
        var worker = new HeartbeatWorker(
            NullLogger<HeartbeatWorker>.Instance,
            Options.Create(options),
            services,
            db);
        var token = db.SavePricingDiscoveryCandidate(
            Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.xlsx"));
        var response = BuildAuthorizedPricingResponseJson(db, new
        {
            pricingCandidateToken = token,
            commandId = "30000000-0000-4000-8000-000000000009",
        });
        var sc = response.GetProperty("data").GetProperty("signedCommand");
        RegisterPricingCommandForDirectInvocation(outbox, sc);

        var invocation = (Task)_runPricingMethod.Invoke(
            worker, new object[] { sc, CancellationToken.None })!;
        await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (scenario == "cancel")
            coordinator.ApplyControl(AutopilotControlAction.Pause, "test_pause");
        await invocation.WaitAsync(TimeSpan.FromSeconds(5));

        var receipt = Assert.Single(db.GetPendingAutonomyEvidence(10)).Signed.Receipt;
        Assert.Equal(expectedResult, receipt.SemanticResult);
        Assert.False(receipt.Clean);
        Assert.Equal(0, receipt.LocalStreak);

        services.Dispose();
        try { File.Delete(dbPath); } catch { }
    }

    [Theory]
    [InlineData("legacy-command")]
    [InlineData("30000000-0000-1000-8000-000000000010")]
    [InlineData("30000000-0000-4000-7000-000000000010")]
    [InlineData("30000000-0000-4000-8000-00000000001A")]
    public async Task RunPricingJob_InvalidCommandAuthority_NeverStartsExecutor(
        string commandId)
    {
        var token = _db.SavePricingDiscoveryCandidate(
            Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.xlsx"));
        var response = BuildAuthorizedPricingResponseJson(_db, new
        {
            pricingCandidateToken = token,
            commandId,
        });
        var sc = response.GetProperty("data").GetProperty("signedCommand");

        await InvokeRunPricingAsync(sc);

        Assert.Empty(_pricingJobExecutor.Specs);
    }

    private JsonElement BuildAuthorizedPricingResponseJson(
        AgentStateDb db,
        object data,
        PricingCostBasisAuthority? installedAuthority = null)
    {
        var authority = installedAuthority ?? PricingTestAuthority.InstallAuthority(
            db,
            PricingTestAuthority.Contract(),
            pharmacyId: TestPharmacyId,
            agentId: TestAgentId,
            machineFingerprint: TestFingerprint);
        var payload = JsonSerializer.SerializeToNode(data)?.AsObject()
            ?? throw new InvalidOperationException("pricing_test_payload_invalid");
        payload["approvalId"] = authority.ApprovalId;
        payload["grantDigest"] = authority.ApprovalDigest;
        return BuildResponseJson("run_pricing_job", payload);
    }

    private JsonElement BuildAuthorizedGeneratedPricingResponseJson(
        AgentStateDb db,
        string commandId)
    {
        var authority = PricingTestAuthority.InstallAuthority(
            db,
            PricingTestAuthority.Contract(),
            pharmacyId: TestPharmacyId,
            agentId: TestAgentId,
            machineFingerprint: TestFingerprint);
        return BuildResponseJson(
            "find_and_run_pricing_job",
            new JsonObject
            {
                ["pack"] = "pharmacy_rx_generate_v2",
                ["commandId"] = commandId,
                ["approvalId"] = authority.ApprovalId,
                ["grantDigest"] = authority.ApprovalDigest,
            });
    }

}
