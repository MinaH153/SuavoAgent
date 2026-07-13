using System.Reflection;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Core.Behavioral;
using SuavoAgent.Core.Learning;
using SuavoAgent.Core.Workers;
using Xunit;

namespace SuavoAgent.Core.Tests.Workers;

public partial class LearningWorkerTests
{
    [Fact]
    public async Task EnabledWorker_PreCancelledRunStillCreatesAuditedSessionAndCleansObservers()
    {
        using var services = WorkerServices();
        var worker = CreateWorker(sp: services);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await ExecuteDirectAsync(worker, cancellation.Token);

        var sessionId = _db.GetActiveSessionId(_options.PharmacyId!);
        Assert.NotNull(sessionId);
        Assert.StartsWith("learn-test-agent-001-", sessionId, StringComparison.Ordinal);
        Assert.True(_db.GetLearningAuditCount(sessionId!) >= 1);
    }

    [Theory]
    [InlineData("pattern")]
    [InlineData("model")]
    public async Task EnabledWorker_ResumesExistingSeedBindingWithoutMintingSecondSession(string phase)
    {
        const string sessionId = "learning-worker-resume-seed";
        _db.CreateLearningSession(sessionId, _options.PharmacyId!);
        _db.UpdateLearningPhase(sessionId, "pattern");
        if (phase == "model")
            _db.UpdateLearningPhase(sessionId, "model");
        var seed = CreateFakeSeedResponse("seed-binding-digest", phase);
        var applicator = new SeedApplicator(_db);
        if (phase == "pattern")
            applicator.ApplyPatternSeeds(sessionId, seed);
        else
            applicator.ApplyModelSeeds(sessionId, seed, applyFleetRxQueueShape: true);
        using var services = WorkerServices();
        var worker = CreateWorker(sp: services);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await ExecuteDirectAsync(worker, cancellation.Token);

        Assert.Equal(sessionId, _db.GetActiveSessionId(_options.PharmacyId!));
        Assert.Equal("seed-binding-digest", Field<string>(worker, "_activeSeedDigest"));
        Assert.Equal("seed-binding-digest", Field<string>(worker, "_lastSeedDigest"));
    }

    [Fact]
    public async Task PullSeeds_WithoutCloudClientIsExplicitNoOp()
    {
        var worker = CreateWorker();

        await InvokeTaskAsync(worker, "PullSeedsAsync", "pattern", CancellationToken.None);

        Assert.Null(Field<string>(worker, "_activeSeedDigest"));
        Assert.Null(Field<string>(worker, "_lastSeedDigest"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Rx")]
    [InlineData("dbo.Rx.extra")]
    [InlineData("dbo.Rx;DROP TABLE Rx")]
    [InlineData("dbo.[Rx]")]
    public async Task DistinctStatusQuery_RejectsUnsafeTableIdentifierBeforeSqlExecution(string table)
    {
        await using var connection = new SqlConnection();

        var values = await InvokeStaticTaskAsync<IReadOnlyList<(string Value, string DisplayName)>>(
            "QueryDistinctStatusValuesAsync",
            connection,
            table,
            "Status",
            CancellationToken.None);

        Assert.Empty(values);
    }

    [Fact]
    public void CanaryHoldCheck_ReflectsExactPharmacyScopedState()
    {
        var worker = CreateWorker();
        Assert.False(InvokeBool(worker, "IsCanaryInHold"));

        _db.UpsertCanaryHold(_options.PharmacyId!, "pioneerrx", "warning", "baseline");

        Assert.True(InvokeBool(worker, "IsCanaryInHold"));
    }

    [Fact]
    public void LocalPmsFingerprint_UsesConservativeSentinelsWithoutCanaryBaseline()
    {
        var worker = CreateWorker();

        var fingerprint = Assert.IsType<SuavoAgent.Contracts.Learning.PmsVersionFingerprint>(
            Invoke(worker, "BuildLocalPmsVersionFingerprint"));

        Assert.Equal("PioneerRx", fingerprint.PmsType);
        Assert.Equal("unestablished", fingerprint.SchemaHash);
        Assert.Equal("unestablished", fingerprint.UiaDialectHash);
        Assert.Null(fingerprint.ProductVersionString);
    }

    [Theory]
    [InlineData(true, true, "assist", 0)]
    [InlineData(true, false, "assist", 0)]
    [InlineData(true, true, "capture", 0)]
    public void TemplateExtraction_EmptyCaptureDoesNotInventRuleFiles(
        bool enabled,
        bool ruleGeneration,
        string mode,
        int expectedTemplates)
    {
        const string sessionId = "learning-template-empty";
        _db.CreateLearningSession(sessionId, _options.PharmacyId!);
        _options.TemplateLearning.Enabled = enabled;
        _options.TemplateLearning.RuleGeneration = ruleGeneration;
        _options.TemplateLearning.Mode = mode;
        var worker = CreateWorker();
        SetField(worker, "_sessionId", sessionId);

        Invoke(worker, "TryExtractAndEmitTemplates", true);

        Assert.Equal(expectedTemplates, _db.GetWorkflowTemplateCount());
    }

    private ServiceProvider WorkerServices()
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddProvider(NullLoggerProvider.Instance));
        services.AddSingleton(new BehavioralEventReceiver(
            _db,
            () => _db.GetActiveSessionId(_options.PharmacyId!)));
        return services.BuildServiceProvider();
    }

    private static async Task ExecuteDirectAsync(LearningWorker worker, CancellationToken ct)
    {
        var method = typeof(LearningWorker).GetMethod(
            "ExecuteAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        await Assert.IsAssignableFrom<Task>(method!.Invoke(worker, new object[] { ct }));
    }

    private static async Task InvokeTaskAsync(object target, string name, params object?[] args) =>
        await Assert.IsAssignableFrom<Task>(Invoke(target, name, args));

    private static async Task<T> InvokeStaticTaskAsync<T>(string name, params object?[] args)
    {
        var method = typeof(LearningWorker).GetMethod(
            name,
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return await Assert.IsAssignableFrom<Task<T>>(method!.Invoke(null, args));
    }

    private static bool InvokeBool(object target, string name) =>
        Assert.IsType<bool>(Invoke(target, name));

    private static object? Invoke(object target, string name, params object?[] args)
    {
        var method = target.GetType().GetMethod(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return method!.Invoke(target, args);
    }

    private static T? Field<T>(object target, string name)
    {
        var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return (T?)field!.GetValue(target);
    }

    private static void SetField(object target, string name, object? value)
    {
        var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(target, value);
    }
}
