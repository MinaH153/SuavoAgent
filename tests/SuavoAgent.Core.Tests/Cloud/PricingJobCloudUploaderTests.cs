using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Data.Sqlite;
using SuavoAgent.Contracts.Learning;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Pricing;
using SuavoAgent.Core.State;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Tests.Pricing;
using Xunit;

namespace SuavoAgent.Core.Tests.Cloud;

public sealed partial class PricingJobCloudUploaderTests : IDisposable
{
    private const string AgentId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
    private const string PharmacyId = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"suavo_pricing_upload_{Guid.NewGuid():N}");
    private readonly AgentStateDb _db;

    public PricingJobCloudUploaderTests()
    {
        Directory.CreateDirectory(_tempDir);
        _db = new AgentStateDb(Path.Combine(_tempDir, "state.db"));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private (PricingJobSpec Spec, PricingJobExecutionResult Execution) CompletedSpec(
        string jobId,
        string? commandId = null)
    {
        var spec = AuthorizePricingJob(_db, new PricingJobSpec(
            jobId, @"C:\Pricing.xlsx", "NDC", "Supplier", "Cost"));
        if (commandId is not null)
            PreparePricingCommandDelivery(_db, commandId, spec);
        _db.UpsertPricingJob(spec, PricingJobStatus.Running, 1, 0, 0);
        _db.SavePricingResult(new SupplierPriceResult(
            jobId, 2, "55111064501", true, "McKesson", 1.25m, null));
        _db.UpsertPricingJob(spec, PricingJobStatus.Completed, 1, 1, 0);
        return (
            spec,
            new PricingJobExecutionResult(
                new PricingJobProgress(jobId, 1, 1, 0, PricingJobStatus.Completed),
                "sql",
                true,
                null));
    }

    private static PricingJobCloudUploader CreateUploader(
        IPostSigner signer,
        AgentStateDb db,
        TimeProvider? clock = null) => new(
            signer,
            db,
            NullLogger<PricingJobCloudUploader>.Instance,
            PricingTestAuthority.TrustedPublicKeys,
            clock);

    private static void RegisterPricingCommandBinding(
        AgentStateDb db,
        string commandId,
        PricingJobSpec spec)
    {
        Assert.NotNull(spec.ApprovalId);
        Assert.NotNull(spec.GrantDigest);
        Assert.True(db.TryRecordNonceAndRegisterPricingIntent(
            Guid.NewGuid().ToString("N"),
            commandId,
            "run_pricing_job",
            Guid.NewGuid().ToString("N"),
            verifiedCommand: null,
            spec.ApprovalId,
            spec.GrantDigest));
    }

    private static void PreparePricingCommandDelivery(
        AgentStateDb db,
        string commandId,
        PricingJobSpec spec,
        string sourceMode = "sql")
    {
        RegisterPricingCommandBinding(db, commandId, spec);
        db.PreparePricingResultDelivery(
            spec,
            commandId,
            sourceUploadId: null,
            sourceMode);
    }

    private static PricingJobSpec AuthorizePricingJob(
        AgentStateDb db,
        PricingJobSpec spec,
        string modality = "sql",
        DateTimeOffset? now = null)
    {
        var evaluatedAt = (now ?? DateTimeOffset.UtcNow).ToUniversalTime();
        var contract = PricingTestAuthority.Contract(modality: modality);
        var authority = PricingTestAuthority.InstallAuthority(
            db,
            contract,
            evaluatedAt);
        var boundSpec = spec with
        {
            ApprovalId = authority.ApprovalId,
            GrantDigest = authority.ApprovalDigest,
        };
        db.UpsertPricingJob(
            boundSpec, PricingJobStatus.Pending, 0, 0, 0);
        Assert.True(db.TryBindPricingInputIdentity(
            boundSpec.JobId,
            new string('a', 64),
            new string('b', 64),
            contract,
            authority,
            evaluatedAt,
            out var code), code);
        return boundSpec;
    }

    private int CountRows(string table, string jobId)
    {
        using var connection = new SqliteConnection(
            $"Data Source={Path.Combine(_tempDir, "state.db")}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = table switch
        {
            "pricing_jobs" =>
                "SELECT count(*) FROM pricing_jobs WHERE job_id = @job",
            "pricing_result_delivery_intents" =>
                "SELECT count(*) FROM pricing_result_delivery_intents WHERE job_id = @job",
            _ => throw new ArgumentOutOfRangeException(nameof(table)),
        };
        command.Parameters.AddWithValue("@job", jobId);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static string InsertLegacyOutbox(
        string path,
        string jobId,
        string payload,
        int itemCount)
    {
        var digest = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO pricing_result_outbox (
                job_id, command_id, payload_json, payload_sha256, item_count,
                execution_ok, state, attempt_count, next_attempt_at, created_at
            ) VALUES (
                @job, 'legacy-command', @payload, @digest, @count,
                0, 'pending', 0, @now, @now
            )
            """;
        command.Parameters.AddWithValue("@job", jobId);
        command.Parameters.AddWithValue("@payload", payload);
        command.Parameters.AddWithValue("@digest", digest);
        command.Parameters.AddWithValue("@count", itemCount);
        command.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("o"));
        Assert.Equal(1, command.ExecuteNonQuery());
        return digest;
    }

    private static VerifiedCloudPostResponse VerifiedResponse(
        int status,
        string body) => new(
        status,
        body,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)))
            .ToLowerInvariant(),
        RemoteCommandTrust.CommandV1KeyId,
        Convert.ToBase64String(new byte[64]));

    private sealed class RecordingPostSigner : IPostSigner
    {
        public string? BoundAgentInstanceId => AgentId;
        public string? BoundPharmacyId => PharmacyId;
        public string? Path { get; private set; }
        public object? Payload { get; private set; }

        public Task<JsonElement?> PostSignedAsync(string path, object payload, CancellationToken ct)
        {
            Path = path;
            Payload = payload;
            var jobId = path.Split('/', StringSplitOptions.RemoveEmptyEntries)[3];
            var recorded = JsonSerializer.SerializeToElement(payload)
                .GetProperty("items")
                .GetArrayLength();
            var commandId = JsonSerializer.SerializeToElement(payload)
                .GetProperty("commandId").GetString();
            return Task.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(new
            {
                schemaVersion = 1,
                kind = "pricing_result_receipt",
                accepted = true,
                commandId,
                agentInstanceId = AgentId,
                pharmacyId = PharmacyId,
                jobId,
                recorded,
                idempotent = false,
            }));
        }

        public Task<JsonElement?> PostSignedVerifiedAsync(
            string path,
            object payload,
            string publicKeyDer,
            CancellationToken ct) =>
            PostSignedAsync(path, payload, ct);

        public Task<VerifiedCloudPostResponse?> PostSignedResponseVerifiedAsync(
            string path,
            object payload,
            CancellationToken ct)
        {
            Path = path;
            Payload = payload;
            var jobId = path.Split('/', StringSplitOptions.RemoveEmptyEntries)[3];
            var recorded = JsonSerializer.SerializeToElement(payload)
                .GetProperty("items").GetArrayLength();
            var commandId = JsonSerializer.SerializeToElement(payload)
                .GetProperty("commandId").GetString();
            var body = JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                kind = "pricing_result_receipt",
                accepted = true,
                commandId,
                agentInstanceId = AgentId,
                pharmacyId = PharmacyId,
                jobId,
                recorded,
                idempotent = false,
            });
            return Task.FromResult<VerifiedCloudPostResponse?>(
                VerifiedResponse(200, body));
        }
    }

    private class ThrowingSigner : IPostSigner
    {
        public string? BoundAgentInstanceId => AgentId;
        public string? BoundPharmacyId => PharmacyId;
        internal ThrowingSigner(int status, string responseBody)
        {
            Status = status;
            ResponseBody = responseBody;
        }

        internal int Status { get; }
        internal string ResponseBody { get; }
        internal int CallCount { get; private set; }

        public Task<JsonElement?> PostSignedAsync(
            string path,
            object payload,
            CancellationToken ct)
        {
            CallCount++;
            throw CloudErrorResponse.Create(
                "cloud_request_failed",
                (HttpStatusCode)Status,
                ResponseBody);
        }

        public Task<JsonElement?> PostSignedVerifiedAsync(
            string path,
            object payload,
            string publicKeyDer,
            CancellationToken ct) => PostSignedAsync(path, payload, ct);

        public Task<VerifiedCloudPostResponse?> PostSignedResponseVerifiedAsync(
            string path,
            object payload,
            CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult<VerifiedCloudPostResponse?>(
                VerifiedResponse(Status, ResponseBody));
        }
    }

    private sealed class TerminalRejectingSigner : ThrowingSigner
    {
        internal TerminalRejectingSigner(string code, int status, string error)
            : base(status, JsonSerializer.Serialize(new
            {
                accepted = false,
                terminal = true,
                code,
                error,
            }))
        {
        }
    }

    private sealed class RejectOncePostSigner : IPostSigner
    {
        public string? BoundAgentInstanceId => AgentId;
        public string? BoundPharmacyId => PharmacyId;
        internal List<string> Payloads { get; } = [];

        public Task<JsonElement?> PostSignedAsync(
            string path,
            object payload,
            CancellationToken ct)
        {
            Payloads.Add(JsonSerializer.Serialize(payload));
            var jobId = path.Split('/', StringSplitOptions.RemoveEmptyEntries)[3];
            var recorded = JsonSerializer.SerializeToElement(payload)
                .GetProperty("items").GetArrayLength();
            var commandId = JsonSerializer.SerializeToElement(payload)
                .GetProperty("commandId").GetString();
            return Task.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(new
            {
                schemaVersion = 1,
                kind = "pricing_result_receipt",
                accepted = Payloads.Count > 1,
                commandId,
                agentInstanceId = AgentId,
                pharmacyId = PharmacyId,
                jobId,
                recorded,
                idempotent = false,
            }));
        }

        public Task<JsonElement?> PostSignedVerifiedAsync(
            string path,
            object payload,
            string publicKeyDer,
            CancellationToken ct) => PostSignedAsync(path, payload, ct);

        public async Task<VerifiedCloudPostResponse?> PostSignedResponseVerifiedAsync(
            string path,
            object payload,
            CancellationToken ct)
        {
            var response = await PostSignedAsync(path, payload, ct);
            return response is null
                ? null
                : VerifiedResponse(200, response.Value.GetRawText());
        }
    }
}
