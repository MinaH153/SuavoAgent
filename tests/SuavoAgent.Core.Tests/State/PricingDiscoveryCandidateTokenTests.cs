using Microsoft.Data.Sqlite;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.State;

public sealed class PricingDiscoveryCandidateTokenTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(),
        $"suavo_pricing_candidate_{Guid.NewGuid():N}.db");

    [Fact]
    public void Candidate_token_is_opaque_one_use_and_does_not_persist_filename()
    {
        using var db = new AgentStateDb(_path);
        var token = db.SavePricingDiscoveryCandidate(@"C:\Pricing\safe.xlsx");

        Assert.Matches("^pdc_[0-9a-f]{32}$", token);
        Assert.Equal(@"C:\Pricing\safe.xlsx", db.TryResolvePricingDiscoveryCandidate(token));
        Assert.Null(db.TryResolvePricingDiscoveryCandidate(token));

        using var connection = new SqliteConnection($"Data Source={_path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM pricing_discovery_candidates";
        Assert.Equal(0L, (long)command.ExecuteScalar()!);
        command.CommandText = "PRAGMA table_info(pricing_discovery_candidates)";
        using var columns = command.ExecuteReader();
        var names = new List<string>();
        while (columns.Read()) names.Add(columns.GetString(1));
        Assert.DoesNotContain("file_name", names);
    }

    [Fact]
    public void Expired_candidate_token_fails_closed()
    {
        using var db = new AgentStateDb(_path);
        var token = db.SavePricingDiscoveryCandidate(
            @"C:\Pricing\safe.xlsx",
            DateTimeOffset.UtcNow - TimeSpan.FromMinutes(11));

        Assert.Null(db.TryResolvePricingDiscoveryCandidate(token));

        using var verification = new SqliteConnection($"Data Source={_path}");
        verification.Open();
        using var count = verification.CreateCommand();
        count.CommandText =
            "SELECT count(*) FROM pricing_discovery_candidates WHERE token = @token";
        count.Parameters.AddWithValue("@token", token);
        Assert.Equal(0L, (long)count.ExecuteScalar()!);
    }

    [Fact]
    public void Candidate_path_does_not_survive_process_state_restart()
    {
        string token;
        using (var first = new AgentStateDb(_path))
        {
            token = first.SavePricingDiscoveryCandidate(
                @"C:\Patients\Jane-Doe-HIV.xlsx");
        }

        using var restarted = new AgentStateDb(_path);
        Assert.Null(restarted.TryResolvePricingDiscoveryCandidate(token));

        using var verification = new SqliteConnection($"Data Source={_path}");
        verification.Open();
        using var count = verification.CreateCommand();
        count.CommandText = "SELECT count(*) FROM pricing_discovery_candidates";
        Assert.Equal(0L, (long)count.ExecuteScalar()!);
    }

    [Fact]
    public async Task Candidate_operations_share_the_connection_wide_lock()
    {
        using var db = new AgentStateDb(_path);

        var operations = Enumerable.Range(0, 8).Select(async worker =>
        {
            for (var index = 0; index < 50; index++)
            {
                var token = db.SavePricingDiscoveryCandidate(
                    $@"C:\Pricing\safe-{worker}-{index}.xlsx");
                Assert.NotNull(db.TryResolvePricingDiscoveryCandidate(token));
                _ = db.GetVerifiedSkillCountForTask("pharmacy", "pricing");
                await Task.Yield();
            }
        });

        await Task.WhenAll(operations);
    }

    [Fact]
    public void Upgrade_purges_legacy_candidate_paths_and_filename_metadata()
    {
        using (var legacy = new SqliteConnection($"Data Source={_path}"))
        {
            legacy.Open();
            using var setup = legacy.CreateCommand();
            setup.CommandText = """
                CREATE TABLE pricing_discovery_candidates (
                    token TEXT PRIMARY KEY,
                    absolute_path TEXT NOT NULL,
                    file_name TEXT,
                    created_at TEXT DEFAULT (datetime('now'))
                );
                INSERT INTO pricing_discovery_candidates (
                    token, absolute_path, file_name, created_at
                ) VALUES (
                    'pdc_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
                    'C:\\Patients\\Jane-Doe-HIV.xlsx',
                    'Jane-Doe-HIV.xlsx',
                    datetime('now', '-1 minute')
                );
                """;
            setup.ExecuteNonQuery();
        }

        using var db = new AgentStateDb(_path);
        using var verification = new SqliteConnection($"Data Source={_path}");
        verification.Open();
        using var count = verification.CreateCommand();
        count.CommandText = "SELECT count(*) FROM pricing_discovery_candidates";
        Assert.Equal(0L, (long)count.ExecuteScalar()!);
        count.CommandText = """
            SELECT count(*) FROM pragma_table_info('pricing_discovery_candidates')
            WHERE name = 'file_name'
            """;
        Assert.Equal(0L, (long)count.ExecuteScalar()!);
    }

    public void Dispose()
    {
        try { File.Delete(_path); } catch { }
    }
}
