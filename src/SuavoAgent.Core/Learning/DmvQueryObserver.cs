using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using SuavoAgent.Core.State;

namespace SuavoAgent.Core.Learning;

/// <summary>
/// Polls sys.dm_exec_query_stats (DMV) for recently executed queries and feeds them
/// through the fail-closed SqlTokenizer pipeline. Any query that contains string
/// literals, hex literals, or bare numeric literals is silently discarded — only
/// structurally-safe parameterized shapes reach the database.
///
/// CRITICAL: Raw SQL text from sys.dm_exec_sql_text may contain PHI (patient names,
/// DOBs, Rx numbers). SqlTokenizer.TryNormalize is the mandatory gate — null means
/// unsafe, never persist raw text.
/// </summary>
public sealed class DmvQueryObserver : ILearningObserver
{
    private readonly AgentStateDb _db;
    private readonly Func<SqlConnection> _connFactory;
    private readonly ILogger<DmvQueryObserver> _logger;

    private volatile bool _running;
    private int _eventsCollected;
    private DateTimeOffset _lastActivity;
    private DateTimeOffset _lastPoll = DateTimeOffset.MinValue;
    private DateTimeOffset _lastClockCalibration = DateTimeOffset.MinValue;

    public string Name => "dmv-query";
    public ObserverPhase ActivePhases => ObserverPhase.Pattern | ObserverPhase.Model;
    public bool HasDmvAccess { get; private set; }
    public int ClockOffsetMs { get; private set; }

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ClockCalibrationInterval = TimeSpan.FromHours(1);

    public DmvQueryObserver(AgentStateDb db, Func<SqlConnection> connFactory,
        ILogger<DmvQueryObserver> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _connFactory = connFactory ?? throw new ArgumentNullException(nameof(connFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    // ── ILearningObserver ──

    public async Task StartAsync(string sessionId, CancellationToken ct)
    {
        _running = true;

        // One-shot DMV access check — if it fails we go dormant forever
        if (!await CheckDmvAccessAsync())
        {
            _logger.LogInformation(
                "DmvQueryObserver: DMV access unavailable for session {Session} — going dormant",
                sessionId);
            HasDmvAccess = false;
            return;
        }

        HasDmvAccess = true;
        _logger.LogInformation(
            "DmvQueryObserver started for session {Session}", sessionId);

        await CalibrateClockAsync();
        _lastClockCalibration = DateTimeOffset.UtcNow;

        while (!ct.IsCancellationRequested && _running)
        {
            var now = DateTimeOffset.UtcNow;

            // Hourly clock recalibration
            if (now - _lastClockCalibration >= ClockCalibrationInterval)
            {
                await CalibrateClockAsync();
                _lastClockCalibration = now;
            }

            await PollDmvAsync(sessionId);
            _lastPoll = now;

            await Task.Delay(PollInterval, ct).ConfigureAwait(false);
        }
    }

    public Task StopAsync()
    {
        _running = false;
        return Task.CompletedTask;
    }

    public ObserverHealth CheckHealth() => new(
        Name, _running, _eventsCollected, 0, _lastActivity);

    public void Dispose() => _running = false;

    // ── DMV polling ──

    private async Task PollDmvAsync(string sessionId)
    {
        try
        {
            using var conn = _connFactory();
            await conn.OpenAsync();

            // Fetch queries executed since last poll window.
            // last_execution_time is SQL Server wall-clock — apply clock offset to compare
            // against our UTC anchor. We filter conservatively: include anything seen in
            // roughly the last poll interval plus a 5-second buffer.
            var cutoff = _lastPoll == DateTimeOffset.MinValue
                ? DateTimeOffset.UtcNow.AddSeconds(-30)   // first poll: 30-second lookback
                : _lastPoll.AddSeconds(-5);               // subsequent: small overlap window

            // Convert cutoff to SQL Server local time using the stored offset
            var sqlCutoff = cutoff.AddMilliseconds(-ClockOffsetMs);

            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT
                    qs.execution_count,
                    qs.last_execution_time,
                    st.text
                FROM sys.dm_exec_query_stats AS qs
                CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) AS st
                WHERE qs.last_execution_time >= @cutoff
                ORDER BY qs.last_execution_time DESC
                """;
            cmd.Parameters.AddWithValue("@cutoff", sqlCutoff.UtcDateTime);

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var execCount = reader.GetInt32(0);
                var lastExecTime = reader.GetDateTime(1).ToString("o");
                var rawSql = reader.IsDBNull(2) ? null : reader.GetString(2);

                ProcessAndStore(_db, sessionId, rawSql, execCount, lastExecTime, ClockOffsetMs);
                _eventsCollected++;
            }

            _lastActivity = DateTimeOffset.UtcNow;
        }
        catch (Exception ex) when (!IsTransient(ex))
        {
            _logger.LogWarning(ex, "DmvQueryObserver: DMV poll failed for session {Session}", sessionId);
        }
    }

    // ── Clock calibration ──

    private async Task CalibrateClockAsync()
    {
        try
        {
            var before = DateTimeOffset.UtcNow;
            using var conn = _connFactory();
            await conn.OpenAsync();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT GETUTCDATE()";
            var result = await cmd.ExecuteScalarAsync();
            var after = DateTimeOffset.UtcNow;

            if (result is DateTime sqlUtc)
            {
                var roundTrip = (after - before).TotalMilliseconds / 2;
                var sqlOffset = (sqlUtc - before.UtcDateTime).TotalMilliseconds;
                ClockOffsetMs = (int)(sqlOffset - roundTrip);
                _logger.LogDebug(
                    "DmvQueryObserver: clock offset = {OffsetMs}ms", ClockOffsetMs);
            }
        }
        catch (Exception ex)
        {
            // Non-fatal — fall back to offset = 0
            _logger.LogWarning(ex, "DmvQueryObserver: clock calibration failed, using offset=0");
            ClockOffsetMs = 0;
        }
    }

    // ── DMV access probe ──

    private async Task<bool> CheckDmvAccessAsync()
    {
        try
        {
            using var conn = _connFactory();
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT TOP 1 1 FROM sys.dm_exec_query_stats";
            await cmd.ExecuteScalarAsync();
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ── Core pipeline (static for testability) ──

    /// <summary>
    /// Runs raw SQL through the fail-closed SqlTokenizer, computes a SHA-256 shape hash,
    /// and persists the observation. No SQL Server connection required — fully testable.
    /// </summary>
    public static void ProcessAndStore(AgentStateDb db, string sessionId,
        string? rawSql, int executionCount, string lastExecutionTime, int clockOffsetMs)
    {
        if (string.IsNullOrWhiteSpace(rawSql)) return;

        var normalized = SqlTokenizer.TryNormalize(rawSql);
        if (normalized is null) return;   // fail-closed: unsafe or unrecognized → discard

        var shapeHash = ComputeShapeHash(normalized.NormalizedShape);
        var tablesJson = JsonSerializer.Serialize(normalized.TablesReferenced);

        db.UpsertDmvQueryObservation(
            sessionId:        sessionId,
            queryShapeHash:   shapeHash,
            queryShape:       normalized.NormalizedShape,
            tablesReferenced: tablesJson,
            isWrite:          normalized.IsWrite,
            executionCount:   executionCount,
            lastExecutionTime: lastExecutionTime,
            clockOffsetMs:    clockOffsetMs);
    }

    // ── Helpers ──

    private static string ComputeShapeHash(string shape)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(shape));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static bool IsTransient(Exception ex) =>
        ex is OperationCanceledException or TaskCanceledException;
}
