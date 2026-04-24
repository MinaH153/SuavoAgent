using SuavoAgent.Core.Audit;
using Xunit;

namespace SuavoAgent.Core.Tests.Mission;

public sealed class AuditChainTests
{
    private static IReadOnlyDictionary<string, object?> Meta(params (string, object?)[] pairs)
    {
        var d = new Dictionary<string, object?>();
        foreach (var (k, v) in pairs) d[k] = v;
        return d;
    }

    [Fact]
    public void EmptyChain_Verifies()
    {
        var chain = new AuditChain();
        Assert.True(chain.VerifyChain());
        Assert.Equal(0, chain.Count);
    }

    [Fact]
    public void SingleEntry_Verifies_AndLinksToGenesis()
    {
        var chain = new AuditChain();
        var entry = chain.Append(
            eventType: "agent.started",
            actor: "SuavoAgent.Core",
            subjectType: "service",
            subjectId: "core",
            metadata: Meta(("version", "3.12.0")));

        Assert.Equal(0, entry.SequenceNumber);
        Assert.Equal(AuditChain.GenesisPreviousHash, entry.PreviousEntryHash);
        Assert.False(string.IsNullOrWhiteSpace(entry.EntryHash));
        Assert.NotEqual(AuditChain.GenesisPreviousHash, entry.EntryHash);
        Assert.True(chain.VerifyChain());
    }

    [Fact]
    public void Hundred_EntryChain_Verifies()
    {
        var chain = new AuditChain();
        for (var i = 0; i < 100; i++)
        {
            chain.Append(
                eventType: "heartbeat.emitted",
                actor: "agent",
                subjectType: "heartbeat",
                subjectId: i.ToString(),
                metadata: Meta(("index", i), ("tick", $"tick-{i}")));
        }
        Assert.Equal(100, chain.Count);
        Assert.True(chain.VerifyChain());
    }

    [Fact]
    public void Chain_IsMonotonic_SequenceNumbersIncreaseWithoutGaps()
    {
        var chain = new AuditChain();
        for (var i = 0; i < 10; i++)
        {
            chain.Append("x", "a", "s", i.ToString(), Meta(("i", i)));
        }
        var snapshot = chain.Snapshot();
        for (var i = 0; i < snapshot.Count; i++)
        {
            Assert.Equal(i, snapshot[i].SequenceNumber);
        }
        for (var i = 1; i < snapshot.Count; i++)
        {
            Assert.Equal(snapshot[i - 1].EntryHash, snapshot[i].PreviousEntryHash);
        }
    }

    [Fact]
    public void TamperedEntryMetadata_FailsVerification()
    {
        var chain = new AuditChain();
        chain.Append("a", "x", "s", "1", Meta(("k", "v")));
        chain.Append("b", "x", "s", "2", Meta(("k", "v")));
        chain.Append("c", "x", "s", "3", Meta(("k", "v")));

        var snap = chain.Snapshot();
        // Tamper with entry 1's metadata but keep hashes in place.
        var tampered = snap[1] with
        {
            Metadata = Meta(("k", "EVIL")),
        };
        chain.ReplaceEntryForTest(1, tampered);

        Assert.False(chain.VerifyChain());
    }

    [Fact]
    public void TamperedPreviousEntryHash_FailsVerification()
    {
        var chain = new AuditChain();
        chain.Append("a", "x", "s", "1", Meta());
        chain.Append("b", "x", "s", "2", Meta());

        var snap = chain.Snapshot();
        var tampered = snap[1] with
        {
            PreviousEntryHash = new string('f', 64),
        };
        chain.ReplaceEntryForTest(1, tampered);

        Assert.False(chain.VerifyChain());
    }

    [Fact]
    public void TamperedEntryHash_FailsVerification()
    {
        var chain = new AuditChain();
        chain.Append("a", "x", "s", "1", Meta());

        var snap = chain.Snapshot();
        var tampered = snap[0] with
        {
            EntryHash = new string('0', 64),
        };
        chain.ReplaceEntryForTest(0, tampered);

        Assert.False(chain.VerifyChain());
    }

    [Fact]
    public void CanonicalJson_IsKeyOrderIndependent()
    {
        var d1 = new Dictionary<string, object?>
        {
            ["zebra"] = 1,
            ["apple"] = 2,
            ["mango"] = 3,
        };
        var d2 = new Dictionary<string, object?>
        {
            ["mango"] = 3,
            ["apple"] = 2,
            ["zebra"] = 1,
        };

        var c1 = AuditChain.SortedKeyCanonicalJson(d1);
        var c2 = AuditChain.SortedKeyCanonicalJson(d2);
        Assert.Equal(c1, c2);
    }

    [Fact]
    public void Append_WithEmptyEventType_Throws()
    {
        var chain = new AuditChain();
        Assert.Throws<ArgumentException>(() =>
            chain.Append("", "actor", "subj", "id", Meta()));
    }

    [Fact]
    public void Append_WithNullMetadata_Throws()
    {
        var chain = new AuditChain();
        Assert.Throws<ArgumentNullException>(() =>
            chain.Append("e", "actor", "subj", "id", null!));
    }
}
