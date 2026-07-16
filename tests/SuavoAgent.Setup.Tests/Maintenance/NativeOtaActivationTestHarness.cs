using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Diagnostics.Maintenance;
using SuavoAgent.Setup.Maintenance;
using SuavoAgent.Setup.Verify;

namespace SuavoAgent.Setup.Tests.Maintenance;

internal sealed class NativeOtaActivationTestHarness : IDisposable
{
    public static readonly DateTimeOffset Now =
        new(2026, 7, 11, 12, 0, 0, TimeSpan.Zero);

    private readonly ECDsa _commandKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa _updateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public NativeOtaActivationTestHarness()
    {
        Root = Path.Combine(
            Path.GetTempPath(),
            "suavo-ota-coordinator-" + Guid.NewGuid().ToString("N"));
        InstallDirectory = Path.Combine(Root, "ProgramFiles", "Suavo", "Agent");
        DataDirectory = Path.Combine(Root, "ProgramData", "SuavoAgent");
        UpdateRoot = Path.Combine(Root, "ProgramData", "updates");
        MaintenanceRoot = Path.Combine(Root, "ProgramData", "maintenance");
        Directory.CreateDirectory(InstallDirectory);
        Directory.CreateDirectory(DataDirectory);
        File.WriteAllText(InstalledMaintenancePath, "signed-maintenance");
        WriteIdentity("1.9.0");

        var payloads = new Dictionary<string, byte[]>
        {
            ["SuavoAgent.Core.exe"] = Encoding.UTF8.GetBytes("core-v2"),
            ["SuavoAgent.Broker.exe"] = Encoding.UTF8.GetBytes("broker-v2"),
            ["SuavoAgent.Helper.exe"] = Encoding.UTF8.GetBytes("helper-v2"),
            ["SuavoAgent.Watchdog.exe"] = Encoding.UTF8.GetBytes("watchdog-v2"),
        };
        ManifestText = BuildManifest(payloads);
        var manifestSignature = SignHex(_updateKey, ManifestText);
        var dataJson = JsonSerializer.Serialize(new
        {
            manifest = ManifestText,
            manifestSignature,
            channel = "stable",
        });
        var dataHash = RemoteCommandTrust.ComputeSha256Hex(dataJson);
        const string nonce = "coordinator-nonce-0001";
        const string keyId = "coordinator-command-key";
        Request = new UpdateActivationRequest(
            UpdateActivationContract.SchemaVersion,
            UpdateActivationContract.CommandName,
            "agent-0001",
            "fingerprint-0001",
            Now.ToString("O"),
            nonce,
            keyId,
            SignBase64(
                _commandKey,
                RemoteCommandTrust.BuildCommandCanonical(
                    UpdateActivationContract.CommandName,
                    "agent-0001",
                    "fingerprint-0001",
                    Now.ToString("O"),
                    nonce,
                    dataHash)),
            dataJson,
            dataHash,
            ManifestText,
            manifestSignature,
            UpdateActivationContract.ComputeStagingId(nonce, dataHash),
            Now.ToString("O"));
        PayloadDirectory = UpdateActivationContract.GetIncomingStagingDirectory(
            UpdateRoot,
            Request.StagingId);
        Directory.CreateDirectory(PayloadDirectory);
        Manifest = UpdateActivationContract.ValidateManifest(
            ManifestText,
            manifestSignature,
            Convert.ToBase64String(_updateKey.ExportSubjectPublicKeyInfo())).Manifest!;
        foreach (var file in Manifest.Files)
            File.WriteAllBytes(Path.Combine(PayloadDirectory, file.FileName), payloads[file.FileName]);

        RequestPath = Path.Combine(
            UpdateRoot,
            UpdateActivationContract.ActivationRequestFileName);
        File.WriteAllText(
            RequestPath,
            UpdateActivationContract.Serialize(Request),
            new UTF8Encoding(false));

        Validator = new NativeUpdateClaimValidator(
            new Dictionary<string, string>
            {
                [keyId] = Convert.ToBase64String(_commandKey.ExportSubjectPublicKeyInfo()),
            },
            Convert.ToBase64String(_updateKey.ExportSubjectPublicKeyInfo()));
        Ledger = new AuthoritativeUpdateReplayLedger(Path.Combine(
            MaintenanceRoot,
            UpdateActivationContract.ReplayLedgerFileName));
        ClaimStore = new NativeUpdateClaimStore(
            MaintenanceRoot,
            Validator,
            Ledger,
            lockdown: _ => { },
            sourceUpdateRoot: UpdateRoot);
        PointerStore = new UpdateClaimPointerStore(MaintenanceRoot);
        Runtime = new FakeNativeOtaActivationRuntime(
            InstalledMaintenancePath,
            UpdateRoot);
    }

    public string Root { get; }
    public string InstallDirectory { get; }
    public string DataDirectory { get; }
    public string UpdateRoot { get; }
    public string MaintenanceRoot { get; }
    public string InstalledMaintenancePath =>
        Path.Combine(InstallDirectory, MaintenanceContract.ExecutableName);
    public string RequestPath { get; }
    public string PayloadDirectory { get; }
    public string ManifestText { get; }
    public UpdateActivationRequest Request { get; }
    public UpdatePackageManifest Manifest { get; }
    public NativeUpdateClaimValidator Validator { get; }
    public AuthoritativeUpdateReplayLedger Ledger { get; }
    public NativeUpdateClaimStore ClaimStore { get; }
    public UpdateClaimPointerStore PointerStore { get; }
    public FakeNativeOtaActivationRuntime Runtime { get; }
    public DateTimeOffset CurrentTime { get; set; } = Now;
    public bool RunnerLaunchSucceeds { get; set; } = true;
    public List<ProcessStartInfo> RunnerLaunches { get; } = [];
    public Func<IDisposable> AcquireTransactionLock { get; set; } =
        () => new TestLease();

    public NativeOtaActivationCoordinator CreateCoordinator(bool useRuntime = true)
    {
        var runner = new NativeMaintenanceRunnerStager(
            lockdown: _ => { },
            verifyTrust: _ => new MaintenanceHostTrustResult(
                true,
                MaintenanceTrustSource.SignedOtaManifest,
                "trusted"),
            launch: info =>
            {
                RunnerLaunches.Add(info);
                return RunnerLaunchSucceeds;
            });
        return new NativeOtaActivationCoordinator(
            InstallDirectory,
            DataDirectory,
            UpdateRoot,
            MaintenanceRoot,
            isLocalSystem: () => true,
            verifyHostTrust: _ => new MaintenanceHostTrustResult(
                true,
                MaintenanceTrustSource.SignedOtaManifest,
                "trusted"),
            Validator,
            Ledger,
            ClaimStore,
            PointerStore,
            runner,
            new NativeOtaCohortAssembler(),
            new NativeInstallCoordinator(),
            clock: () => CurrentTime,
            acquireTransactionLock: () => AcquireTransactionLock(),
            runtime: useRuntime ? Runtime : null);
    }

    public (DurableUpdateClaim Claim, UpdateActivationClaimPointer Pointer) ClaimAndBegin()
    {
        var claim = ClaimStore.Claim(
            RequestPath,
            PayloadDirectory,
            new InstalledUpdateIdentity("agent-0001", "fingerprint-0001", "1.9.0"),
            Now);
        if (!claim.Succeeded)
            throw new InvalidOperationException("Could not create test claim: " + claim.Code);
        var pointer = PointerStore.Begin(claim.Claim!, Now);
        return (claim.Claim!, pointer);
    }

    public void WriteIdentity(string version)
    {
        Directory.CreateDirectory(InstallDirectory);
        File.WriteAllText(
            Path.Combine(InstallDirectory, "appsettings.json"),
            JsonSerializer.Serialize(new
            {
                Agent = new
                {
                    AgentId = "agent-0001",
                    MachineFingerprint = "fingerprint-0001",
                    Version = version,
                },
            }));
        File.WriteAllText(
            Path.Combine(InstallDirectory, MaintenanceContract.InstallStateFileName),
            JsonSerializer.Serialize(new { version }));
    }

    public UpdateActivationCompletion ReadCompletion()
    {
        if (!UpdateActivationContract.TryDeserializeCompletion(
                File.ReadAllText(PointerStore.CompletionPath),
                out var completion,
                out var code))
            throw new InvalidDataException("Completion invalid: " + code);
        return completion!;
    }

    private string BuildManifest(IReadOnlyDictionary<string, byte[]> payloads)
    {
        const string root = "https://github.com/SuavoLLC/MKM/releases/download/v2.0.0/";
        return $"{root}SuavoAgent.Core.exe|{Hash(payloads["SuavoAgent.Core.exe"])}|" +
               $"{root}SuavoAgent.Broker.exe|{Hash(payloads["SuavoAgent.Broker.exe"])}|" +
               $"{root}SuavoAgent.Helper.exe|{Hash(payloads["SuavoAgent.Helper.exe"])}|" +
               "2.0.0|net8.0|win-x64|" +
               $"{root}SuavoAgent.Watchdog.exe|{Hash(payloads["SuavoAgent.Watchdog.exe"])}";
    }

    private static string Hash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string SignHex(ECDsa key, string canonical) => Convert.ToHexString(
        key.SignData(
            Encoding.UTF8.GetBytes(canonical),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

    private static string SignBase64(ECDsa key, string canonical) => Convert.ToBase64String(
        key.SignData(
            Encoding.UTF8.GetBytes(canonical),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

    public void Dispose()
    {
        _commandKey.Dispose();
        _updateKey.Dispose();
        try { if (Directory.Exists(Root)) Directory.Delete(Root, true); } catch { }
    }

    internal sealed class TestLease : IDisposable
    {
        public void Dispose() { }
    }
}

internal sealed class FakeNativeOtaActivationRuntime : INativeOtaActivationRuntime
{
    public FakeNativeOtaActivationRuntime(string installedHostPath, string updateRoot)
    {
        InstalledHost = new(NativeOtaActivationCoordinator.Success, installedHostPath);
        RunnerHost = new(NativeOtaActivationCoordinator.Success, installedHostPath);
        Health = new FakeNativeOtaActivationHealth(updateRoot);
    }

    public NativeOtaHostValidation InstalledHost { get; set; }
    public NativeOtaHostValidation RunnerHost { get; set; }
    public bool LeaseAvailable { get; set; } = true;
    public bool TerminateResult { get; set; } = true;
    public bool CurrentCohortHealthy { get; set; } = true;
    public InstallTransactionResult RecoveryResult { get; set; } =
        InstallTransactionResult.Success();
    public OtaCohortAssemblyResult? AssemblyResult { get; set; }
    public InstallTransactionResult TransactionResult { get; set; } =
        InstallTransactionResult.Success();
    public Exception? RecoverException { get; set; }
    public Exception? AssemblyException { get; set; }
    public Exception? ExecuteException { get; set; }
    public Action? BeforeExecuteReturn { get; set; }
    public bool InvokeBeforeActivate { get; set; } = true;
    public bool InvokeHealthVerification { get; set; } = true;
    public int TerminateCalls { get; private set; }
    public int RecoveryCalls { get; private set; }
    public int AssemblyCalls { get; private set; }
    public int ExecuteCalls { get; private set; }
    public FakeNativeOtaActivationHealth Health { get; }

    public NativeOtaHostValidation ValidateInstalledHost() => InstalledHost;

    public NativeOtaHostValidation ValidateRunnerHost() => RunnerHost;

    public IDisposable? TryAcquireRunnerLease(UpdateActivationClaimPointer pointer) =>
        LeaseAvailable ? new NativeOtaActivationTestHarness.TestLease() : null;

    public bool TerminateExactStaleRunner(string expectedRunnerPath)
    {
        TerminateCalls++;
        return TerminateResult;
    }

    public OtaCohortAssemblyResult Assemble(
        DurableUpdateClaim claim,
        string installDirectory,
        string dataDirectory,
        string maintenanceRoot,
        Action progress)
    {
        AssemblyCalls++;
        progress();
        if (AssemblyException is not null) throw AssemblyException;
        return AssemblyResult ?? OtaCohortAssemblyResult.Success(
            NativeInstallCoordinator.CreatePreparation(
                installDirectory,
                dataDirectory,
                maintenanceRoot,
                claim.Validated.Request.StagingId[..32]));
    }

    public InstallTransactionResult RecoverIncomplete(
        string installDirectory,
        string dataDirectory,
        string maintenanceRoot,
        Action progress)
    {
        RecoveryCalls++;
        progress();
        if (RecoverException is not null) throw RecoverException;
        return RecoveryResult;
    }

    public InstallTransactionResult Execute(
        NativeInstallPreparation preparation,
        Func<bool> verifyHealthMilestone,
        Func<bool> beforeActivate,
        Action transactionProgress)
    {
        ExecuteCalls++;
        transactionProgress();
        if (InvokeBeforeActivate && !beforeActivate())
            return InstallTransactionResult.Failed("activation_challenge_failed", false);
        if (ExecuteException is not null) throw ExecuteException;
        if (InvokeHealthVerification && !verifyHealthMilestone())
            return InstallTransactionResult.Failed("health_probation_failed", true);
        BeforeExecuteReturn?.Invoke();
        return TransactionResult;
    }

    public INativeOtaActivationHealth CreateHealth(
        string updateRoot,
        string systemClaimDirectory) => Health;

    public bool IsCurrentCohortHealthy() => CurrentCohortHealthy;
}

internal sealed class FakeNativeOtaActivationHealth : INativeOtaActivationHealth
{
    private readonly string _updateRoot;

    public FakeNativeOtaActivationHealth(string updateRoot) => _updateRoot = updateRoot;

    public bool Passed { get; set; } = true;
    public bool DurableMilestone { get; set; }
    public Exception? IssueException { get; set; }
    public Exception? WaitException { get; set; }
    public int IssueCalls { get; private set; }
    public int WaitCalls { get; private set; }
    public int CleanupCalls { get; private set; }
    public TimeSpan? ObservedTimeout { get; private set; }

    public UpdateActivationHealthChallenge Issue(
        UpdateActivationClaimPointer pointer,
        InstalledUpdateIdentity identity,
        DateTimeOffset now)
    {
        IssueCalls++;
        if (IssueException is not null) throw IssueException;
        Directory.CreateDirectory(_updateRoot);
        File.WriteAllText(
            UpdateActivationContract.DefaultHealthChallengePath(_updateRoot),
            "runtime-challenge");
        File.WriteAllText(
            UpdateActivationContract.DefaultHealthMilestonePath(_updateRoot),
            "runtime-milestone");
        return UpdateActivationContract.CreateHealthChallenge(
            pointer,
            identity.AgentId,
            identity.MachineFingerprint,
            now);
    }

    public Task<VerifyOutcome> WaitAsync(
        UpdateActivationHealthChallenge challenge,
        string installDirectory,
        string dataDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        Action progress)
    {
        WaitCalls++;
        ObservedTimeout = timeout;
        progress();
        if (WaitException is not null) throw WaitException;
        return Task.FromResult(new VerifyOutcome(
            Passed,
            [],
            Passed ? "healthy" : "health probation timed out"));
    }

    public void CleanupRuntimeProofs()
    {
        CleanupCalls++;
        TryDelete(UpdateActivationContract.DefaultHealthChallengePath(_updateRoot));
        TryDelete(UpdateActivationContract.DefaultHealthMilestonePath(_updateRoot));
    }

    public bool HasDurableMilestone(
        UpdateActivationClaimPointer pointer,
        InstalledUpdateIdentity identity,
        DateTimeOffset now) => DurableMilestone;

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
