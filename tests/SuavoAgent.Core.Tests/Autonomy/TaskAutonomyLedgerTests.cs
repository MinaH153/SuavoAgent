using SuavoAgent.Core.Autonomy;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.State;
using SuavoAgent.Core.Workers;
using SuavoAgent.Contracts.Security;
using Xunit;

namespace SuavoAgent.Core.Tests.Autonomy;

public class TaskAutonomyLedgerTests : IDisposable
{
    private readonly AgentStateDb _db = new(":memory:");
    private const string Task = "pricing";
    private const string Pharmacy = "ph-1";

    public void Dispose() => _db.Dispose();

    [Fact]
    public void NeverRun_IsSupervised_AndMayNotRunUnsupervised()
    {
        var ledger = new TaskAutonomyLedger(_db, cleanRunsThreshold: 12);
        var s = ledger.GetState(Task, Pharmacy);
        Assert.Equal(0, s.ConsecutiveCleanRuns);
        Assert.Equal(AutonomyLevel.Supervised, s.Level);
        Assert.False(ledger.MayRunUnsupervised(Task, Pharmacy, unsupervisedExecutionEnabled: true));
    }

    [Fact]
    public void EarnsEligibility_AfterThresholdCleanRuns_PersistedAcrossInstances()
    {
        var ledger = new TaskAutonomyLedger(_db, cleanRunsThreshold: 3);
        ledger.RecordRun(Task, Pharmacy, clean: true);
        ledger.RecordRun(Task, Pharmacy, clean: true);
        var third = ledger.RecordRun(Task, Pharmacy, clean: true, outcome: "completed");

        Assert.Equal(3, third.ConsecutiveCleanRuns);
        Assert.Equal(3, third.TotalRuns);
        Assert.Equal(AutonomyLevel.Eligible, third.Level);

        // A fresh ledger over the same DB sees the earned standing (persistence).
        var reopened = new TaskAutonomyLedger(_db, cleanRunsThreshold: 3);
        Assert.Equal(AutonomyLevel.Eligible, reopened.GetState(Task, Pharmacy).Level);
    }

    [Fact]
    public void OneStumble_ResetsTheStreak_AndDropsBackToSupervised()
    {
        var ledger = new TaskAutonomyLedger(_db, cleanRunsThreshold: 3);
        ledger.RecordRun(Task, Pharmacy, clean: true);
        ledger.RecordRun(Task, Pharmacy, clean: true);
        ledger.RecordRun(Task, Pharmacy, clean: true);
        Assert.Equal(AutonomyLevel.Eligible, ledger.GetState(Task, Pharmacy).Level);

        var afterFail = ledger.RecordRun(Task, Pharmacy, clean: false, outcome: "aborted");
        Assert.Equal(0, afterFail.ConsecutiveCleanRuns);
        Assert.Equal(4, afterFail.TotalRuns); // total still counts the failed run
        Assert.Equal(AutonomyLevel.Supervised, afterFail.Level);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UnsignedLegacyRuns_NeverGrantUnsupervisedAuthority(bool enabled)
    {
        var ledger = new TaskAutonomyLedger(_db, cleanRunsThreshold: 2);
        ledger.RecordRun(Task, Pharmacy, clean: true);
        ledger.RecordRun(Task, Pharmacy, clean: true); // now Eligible
        Assert.False(ledger.MayRunUnsupervised(Task, Pharmacy, enabled));
    }

    [Fact]
    public void Eligible_ButNotEnabled_NeverRunsUnsupervised()
    {
        var ledger = new TaskAutonomyLedger(_db, cleanRunsThreshold: 1);
        ledger.RecordRun(Task, Pharmacy, clean: true); // Eligible immediately at threshold 1
        Assert.Equal(AutonomyLevel.Eligible, ledger.GetState(Task, Pharmacy).Level);
        Assert.False(ledger.MayRunUnsupervised(Task, Pharmacy, unsupervisedExecutionEnabled: false));
    }

    [Fact]
    public void Tasks_AreTrackedIndependently()
    {
        var ledger = new TaskAutonomyLedger(_db, cleanRunsThreshold: 2);
        ledger.RecordRun("pricing", Pharmacy, clean: true);
        ledger.RecordRun("pricing", Pharmacy, clean: true); // pricing Eligible
        ledger.RecordRun("writeback", Pharmacy, clean: true); // writeback only 1

        Assert.Equal(AutonomyLevel.Eligible, ledger.GetState("pricing", Pharmacy).Level);
        Assert.Equal(AutonomyLevel.Supervised, ledger.GetState("writeback", Pharmacy).Level);
    }

    [Fact]
    public void SignedExactScope_RequiresThresholdCurrentKeyAndExplicitEnable()
    {
        using var signer = new EvidenceSigner("a".PadLeft(64, 'a'));
        var options = EvidenceOptions();
        var ledger = new TaskAutonomyLedger(_db, 2, options, signer);
        var scope = EvidenceScope();

        ledger.RecordRun(CleanEvidence(scope, supervised: true));
        Assert.False(ledger.MayRunUnsupervised(scope, options.PharmacyId!, true));
        ledger.RecordRun(CleanEvidence(scope, supervised: true));

        Assert.False(ledger.MayRunUnsupervised(scope, options.PharmacyId!, false));
        Assert.True(ledger.MayRunUnsupervised(scope, options.PharmacyId!, true));
        var pending = _db.GetPendingAutonomyEvidence(10);
        Assert.Equal(2, pending.Count);
        Assert.Equal([1L, 2L], pending.Select(item => item.Signed.Receipt.Counter));
        Assert.All(pending, item =>
        {
            Assert.Equal(scope.ScopeDigest, item.Signed.Receipt.ScopeDigest);
            Assert.True(item.Signed.Receipt.Supervised);
            Assert.True(item.Signed.Receipt.Clean);
        });
    }

    [Fact]
    public void DirtyRunResetsExactScopeAndDeviceKeyChangeCannotInheritStanding()
    {
        var options = EvidenceOptions();
        var scope = EvidenceScope();
        using (var firstKey = new EvidenceSigner("a".PadLeft(64, 'a')))
        {
            var ledger = new TaskAutonomyLedger(_db, 2, options, firstKey);
            ledger.RecordRun(CleanEvidence(scope, supervised: true));
            ledger.RecordRun(CleanEvidence(scope, supervised: true));
            var dirty = ledger.RecordRun(new(
                Guid.NewGuid().ToString("D"),
                scope, false, 1, AutonomySemanticResult.Failed, false,
                new string('e', 64), DateTimeOffset.UtcNow));
            Assert.Equal(0, dirty.ConsecutiveCleanRuns);
            Assert.False(ledger.MayRunUnsupervised(scope, options.PharmacyId!, true));
        }

        using var replacementKey = new EvidenceSigner("b".PadLeft(64, 'b'));
        var replacement = new TaskAutonomyLedger(_db, 2, options, replacementKey);
        var firstReplacementRun = replacement.RecordRun(CleanEvidence(scope, supervised: true));
        Assert.Equal(1, firstReplacementRun.ConsecutiveCleanRuns);
        Assert.Equal(1, firstReplacementRun.TotalRuns);
        Assert.False(replacement.MayRunUnsupervised(scope, options.PharmacyId!, true));
    }

    [Fact]
    public void SignedExactAutoCommand_IsThePricingEnable_EvenWhenLegacyFlagIsOff()
    {
        using var signer = new EvidenceSigner(new string('a', 64));
        var options = EvidenceOptions();
        Assert.False(options.EnableTaskAutonomy);
        var scope = EvidenceScope();
        var ledger = new TaskAutonomyLedger(_db, 1, options, signer);

        Assert.False(HeartbeatWorker.IsPricingAutonomyCommandAllowed(
            AutonomyExecutionMode.Auto, ledger, scope, options.PharmacyId!));
        ledger.RecordRun(CleanEvidence(scope, supervised: true));
        Assert.True(HeartbeatWorker.IsPricingAutonomyCommandAllowed(
            AutonomyExecutionMode.Auto, ledger, scope, options.PharmacyId!));

        var changedScope = scope with { AppVersion = "3.9.3" };
        Assert.False(HeartbeatWorker.IsPricingAutonomyCommandAllowed(
            AutonomyExecutionMode.Auto, ledger, changedScope, options.PharmacyId!));
    }

    [Fact]
    public void ExactLocalEvidenceExpiresAfterSevenDays()
    {
        using var signer = new EvidenceSigner(new string('a', 64));
        var options = EvidenceOptions();
        var scope = EvidenceScope();
        var futureClock = DateTimeOffset.UtcNow.AddDays(8);
        var ledger = new TaskAutonomyLedger(
            _db, 1, options, signer, now: () => futureClock);
        ledger.RecordRun(CleanEvidence(scope, supervised: true));

        Assert.False(ledger.MayRunUnsupervised(scope, options.PharmacyId!, true));
        Assert.False(HeartbeatWorker.IsPricingAutonomyCommandAllowed(
            AutonomyExecutionMode.Auto, ledger, scope, options.PharmacyId!));
    }

    [Fact]
    public void EvidencePersistenceLatch_RequiresLaterDurableSupervisedCleanRecovery()
    {
        using var signer = new EvidenceSigner(new string('a', 64));
        var options = EvidenceOptions();
        var scope = EvidenceScope();
        var ledger = new TaskAutonomyLedger(_db, 1, options, signer);
        ledger.RecordRun(CleanEvidence(scope, supervised: true));
        Assert.True(ledger.MayRunUnsupervised(scope, options.PharmacyId!, true));

        ledger.LatchDisabled("terminal_evidence_persistence_failed");
        Assert.False(ledger.MayRunUnsupervised(scope, options.PharmacyId!, true));

        ledger.RecordRun(new(
            Guid.NewGuid().ToString("D"), scope, true, 0,
            AutonomySemanticResult.Failed, false, new string('e', 64),
            DateTimeOffset.UtcNow));
        Assert.False(ledger.MayRunUnsupervised(scope, options.PharmacyId!, true));

        ledger.RecordRun(CleanEvidence(scope, supervised: false));
        Assert.False(ledger.MayRunUnsupervised(scope, options.PharmacyId!, true));

        ledger.RecordRun(CleanEvidence(scope, supervised: true));
        Assert.True(ledger.MayRunUnsupervised(scope, options.PharmacyId!, true));
    }

    [Fact]
    public void UnprovenPricingAdmission_DurablyLatchesUntilStableTrustedSupervisedCleanRun()
    {
        using var signer = new EvidenceSigner(new string('a', 64));
        var options = EvidenceOptions();
        var trustedScope = EvidenceScope();
        var ledger = new TaskAutonomyLedger(_db, 1, options, signer);
        ledger.RecordRun(CleanEvidence(trustedScope, supervised: true));
        Assert.True(ledger.MayRunUnsupervised(
            trustedScope, options.PharmacyId!, true));

        var unprovenScope = trustedScope with
        {
            AppVersion = "unverified",
            SelectorDigest = new string('9', 64),
        };
        HeartbeatWorker.EnforcePricingAdmissionIdentity(
            new(true, unprovenScope, TrustedIdentity: false),
            reason =>
            {
                ledger.LatchDisabled(reason);
                return true;
            });

        var reopened = new TaskAutonomyLedger(_db, 1, options, signer);
        Assert.False(reopened.MayRunUnsupervised(
            trustedScope, options.PharmacyId!, true));

        reopened.RecordRun(new(
            Guid.NewGuid().ToString("D"), trustedScope, true, 1,
            AutonomySemanticResult.Failed, false, new string('e', 64),
            DateTimeOffset.UtcNow));
        Assert.False(reopened.MayRunUnsupervised(
            trustedScope, options.PharmacyId!, true));

        reopened.RecordRun(CleanEvidence(trustedScope, supervised: false));
        Assert.False(reopened.MayRunUnsupervised(
            trustedScope, options.PharmacyId!, true));

        reopened.RecordRun(CleanEvidence(trustedScope, supervised: true));
        Assert.True(reopened.MayRunUnsupervised(
            trustedScope, options.PharmacyId!, true));
    }

    [Fact]
    public void TerminalRunId_IsExactlyOnceAcrossReceiptAndOutbox()
    {
        using var signer = new EvidenceSigner(new string('a', 64));
        var options = EvidenceOptions();
        var scope = EvidenceScope();
        var ledger = new TaskAutonomyLedger(_db, 1, options, signer);
        var runId = Guid.NewGuid().ToString("D");
        var evidence = new AutonomyRunEvidence(
            runId, scope, true, 0, AutonomySemanticResult.Cancelled, false,
            new string('e', 64), DateTimeOffset.UtcNow);

        ledger.RecordRun(evidence);
        Assert.ThrowsAny<Exception>(() => ledger.RecordRun(evidence));
        var pending = _db.GetPendingAutonomyEvidence(10);
        var receipt = Assert.Single(pending).Signed.Receipt;
        Assert.Equal(runId, receipt.ReceiptId);
        Assert.Equal("cancelled", receipt.SemanticResult);
        Assert.False(receipt.Clean);
    }

    [Fact]
    public void SignOrPersistenceFailure_LatchesOffUntilDurableSupervisedClean()
    {
        using var signer = new EvidenceSigner(new string('a', 64));
        var options = EvidenceOptions();
        var scope = EvidenceScope();
        var ledger = new TaskAutonomyLedger(_db, 1, options, signer);
        ledger.RecordRun(CleanEvidence(scope, supervised: true));
        Assert.True(ledger.MayRunUnsupervised(scope, options.PharmacyId!, true));

        signer.ThrowOnAutonomy = true;
        Assert.Throws<InvalidOperationException>(() =>
            ledger.RecordRun(CleanEvidence(scope, supervised: false)));
        Assert.False(ledger.MayRunUnsupervised(scope, options.PharmacyId!, true));

        signer.ThrowOnAutonomy = false;
        ledger.RecordRun(CleanEvidence(scope, supervised: true));
        Assert.True(ledger.MayRunUnsupervised(scope, options.PharmacyId!, true));
    }

    private static AgentOptions EvidenceOptions() => new()
    {
        AgentId = "11111111-1111-4111-8111-111111111111",
        PharmacyId = "22222222-2222-4222-8222-222222222222",
        MachineFingerprint = "workstation-01",
        Version = "3.9.2",
        EnableTaskAutonomy = false,
    };

    private static AutonomyEvidenceScope EvidenceScope() => TaskAutonomyScope.Create(
        "pricing", "pricing", "pioneerrx", "3.9.2",
        new string('b', 64), new string('c', 64), new string('d', 64),
        PricingExecutorMode.UiaFirst);

    private static AutonomyRunEvidence CleanEvidence(
        AutonomyEvidenceScope scope,
        bool supervised) => new(
            Guid.NewGuid().ToString("D"),
            scope, supervised, 2, AutonomySemanticResult.Completed, true,
            new string('f', 64), DateTimeOffset.UtcNow);

    private sealed class EvidenceSigner(string keyId) : IDeviceAuthoritySigner
    {
        public string KeyId { get; } = keyId;
        internal bool ThrowOnAutonomy { get; set; }

        public SignedDeviceReceipt<AutonomyEvidenceDeviceReceipt> Sign(
            AutonomyEvidenceDeviceReceipt receipt)
        {
            if (ThrowOnAutonomy)
                throw new InvalidOperationException("test_autonomy_sign_failure");
            return new(receipt, KeyId, "device-signature", new string('9', 64));
        }

        public SignedDeviceReceipt<PomActivationDeviceReceipt> Sign(
            PomActivationDeviceReceipt receipt) => throw new NotSupportedException();
        public SignedDeviceReceipt<RxSourceDeviceReceipt> Sign(
            RxSourceDeviceReceipt receipt) => throw new NotSupportedException();
        public SignedDeviceReceipt<SeedApplicationDeviceReceipt> Sign(
            SeedApplicationDeviceReceipt receipt) => throw new NotSupportedException();
        public SignedDeviceProvisioningProof SignProvisioningProof(
            DeviceProvisioningProofPayload proof) => throw new NotSupportedException();
        public SignedDeviceProbationHealth SignProbationHealth(
            DeviceProbationHealthFields health) => throw new NotSupportedException();
        public void Dispose() { }
    }
}
