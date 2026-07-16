using SuavoAgent.Contracts.Behavioral;
using SuavoAgent.Contracts.Learning;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.State;

/// <summary>M2b selector_patches store — round-trip, retire, and INSERT-OR-REPLACE re-activation.</summary>
public class SelectorPatchStoreTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"suavo_sp_{Guid.NewGuid():N}");
    private readonly AgentStateDb _db;

    public SelectorPatchStoreTests()
    {
        Directory.CreateDirectory(_tempDir);
        _db = new AgentStateDb(Path.Combine(_tempDir, "state.db"));
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
        }
    }

    private static SelectorPatch Patch(string id = "p1", int version = 1, string? pms = "v1.2", string? screen = "scrA") =>
        new(id, "skill-x", SelectorStepId.QuickSearchField, pms, screen,
            new ElementSignature("Edit", "ndcSearchBox", "PioneerRxEdit"),
            new[] { new ElementSignature("Edit", "altSearchBox", null) },
            0.85, "seed-digest", version, "pharmacist_in_charge");

    [Fact]
    public void Upsert_GetActive_RoundTrips()
    {
        _db.UpsertSelectorPatch(Patch(), "2026-06-03T00:00:00Z");

        var got = Assert.Single(_db.GetActiveSelectorPatches());
        Assert.Equal("p1", got.PatchId);
        Assert.Equal("skill-x", got.SkillId);
        Assert.Equal(SelectorStepId.QuickSearchField, got.StepId);
        Assert.Equal("v1.2", got.PmsFingerprint);
        Assert.Equal("scrA", got.ScreenSignatureV1);
        Assert.Equal("Edit", got.Target.ControlType);
        Assert.Equal("ndcSearchBox", got.Target.AutomationId);
        Assert.Equal("PioneerRxEdit", got.Target.ClassName);
        Assert.Equal("altSearchBox", Assert.Single(got.Fallbacks).AutomationId);
        Assert.Equal(0.85, got.Confidence, 3);
        Assert.Equal("seed-digest", got.SeedDigest);
        Assert.Equal(1, got.Version);
        Assert.Equal("pharmacist_in_charge", got.ApprovedByRole);
    }

    [Fact]
    public void Retire_ExcludesFromActive()
    {
        _db.UpsertSelectorPatch(Patch(), "t1");
        _db.RetireSelectorPatch("p1", "t2", "confidence_drop");
        Assert.Empty(_db.GetActiveSelectorPatches());
    }

    [Fact]
    public void Upsert_SameId_Replaces_AndReactivates()
    {
        _db.UpsertSelectorPatch(Patch(version: 1), "t1");
        _db.RetireSelectorPatch("p1", "t2", "superseded");
        Assert.Empty(_db.GetActiveSelectorPatches());

        _db.UpsertSelectorPatch(Patch(version: 2), "t3"); // re-apply same id → fresh + active
        var got = Assert.Single(_db.GetActiveSelectorPatches());
        Assert.Equal(2, got.Version);
    }

    [Fact]
    public void NullGatesAndEmptyFallbacks_RoundTrip()
    {
        _db.UpsertSelectorPatch(
            new SelectorPatch("p2", "skill-x", SelectorStepId.PricingTab, null, null,
                new ElementSignature("TabItem", "pricingTab", null),
                Array.Empty<ElementSignature>(), 0.5, "d", 1),
            "t1");

        var got = Assert.Single(_db.GetActiveSelectorPatches());
        Assert.Null(got.PmsFingerprint);
        Assert.Null(got.ScreenSignatureV1);
        Assert.Empty(got.Fallbacks);
    }

    [Fact]
    public void MultipleActivePatches_OrderedNewestVersionFirst()
    {
        _db.UpsertSelectorPatch(Patch(id: "a", version: 1), "t1");
        _db.UpsertSelectorPatch(Patch(id: "b", version: 5), "t2");
        var active = _db.GetActiveSelectorPatches();
        Assert.Equal(2, active.Count);
        Assert.Equal("b", active[0].PatchId); // ORDER BY version DESC
    }

    [Fact]
    public void UpgradedBox_WithMigration1Already_Applied_StillCreatesSelectorPatchesTable()
    {
        // Field bug (2026-06-03, Mina's box): selector_patches was originally added to the BODY of
        // already-applied migration #1, so a box that applied #1 in the v3.12 era NEVER created the table —
        // UpsertSelectorPatch threw `no such table: selector_patches`, making both the operator
        // update_selector correction and the fleet seed-apply dead on every upgraded box. The fresh-DB
        // store tests above passed only because a fresh DB runs migration #1. This simulates the UPGRADED
        // box (migrations 1+2 recorded as applied, no selector_patches present) and proves the new
        // versioned migration #3 creates the table on next startup.
        var upgradeDbPath = Path.Combine(_tempDir, "upgraded.db");
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={upgradeDbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE schema_migrations (version INTEGER PRIMARY KEY, applied_at TEXT NOT NULL, description TEXT NOT NULL);
                INSERT INTO schema_migrations (version, applied_at, description) VALUES (1, '2026-04-01T00:00:00Z', 'v3.12 (pre-M2b: applied WITHOUT selector_patches)');
                INSERT INTO schema_migrations (version, applied_at, description) VALUES (2, '2026-04-01T00:00:00Z', 'v3.12 applied schema adaptations');
                """;
            cmd.ExecuteNonQuery();
        }

        // Opening AgentStateDb runs ApplyMigrationIfNeeded(3) → creates selector_patches on the upgraded DB.
        // Without the fix (table only in already-applied migration #1) this Upsert throws `no such table`.
        using var upgraded = new AgentStateDb(upgradeDbPath);
        upgraded.UpsertSelectorPatch(Patch(id: "upg"), "2026-06-03T00:00:00Z");
        Assert.Equal("upg", Assert.Single(upgraded.GetActiveSelectorPatches()).PatchId);
    }

    [Fact]
    public void UpsertSelectorPatchWithAudit_CommitsBothAtomically_NoNestedTransaction()
    {
        // Bug C regression (Mina's box, 2026-06-03): the operator update_selector handler wrapped
        // UpsertSelectorPatch + AppendChainedAuditEntry in an OUTER BeginTransaction, but
        // AppendChainedAuditEntry opens its OWN Serializable transaction — Microsoft.Data.Sqlite threw
        // `does not support nested transactions` on every real apply, so the patch never persisted.
        // The atomic combined method commits both writes in ONE transaction with no nesting.
        var hash = _db.UpsertSelectorPatchWithAudit(
            Patch(id: "op1"),
            new AuditEntry(
                TaskId: "op1", EventType: "selector_patch_applied", FromState: "proposed", ToState: "active",
                Trigger: "update_selector", CommandId: "cmd-1", RequesterId: "operator",
                Actor: "operator", SourceComponent: "test", CaptureReason: "step=QuickSearchField via=operator"),
            "2026-06-03T12:00:00Z");

        Assert.False(string.IsNullOrEmpty(hash));                                   // chain hash returned
        Assert.Equal("op1", Assert.Single(_db.GetActiveSelectorPatches()).PatchId); // patch persisted
        Assert.Equal(hash, _db.GetLastAuditHash());                                 // audit row appended + chained
    }
}
