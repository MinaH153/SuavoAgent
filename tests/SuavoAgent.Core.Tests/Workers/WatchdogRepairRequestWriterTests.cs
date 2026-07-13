using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Workers;
using Xunit;

namespace SuavoAgent.Core.Tests.Workers;

public sealed class WatchdogRepairRequestWriterTests : IDisposable
{
    private const string Expiry = "2026-07-10T22:04:00.0000000+00:00";
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "suavo-repair-writer-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Queue_preserves_exact_signed_raw_data_and_envelope()
    {
        const string rawData = "{ \"reason\" : \"watchdog_critical\", \"commandId\" : \"cmd-exact-1\", \"expiresAt\" : \"2026-07-10T22:04:00.0000000+00:00\" }";
        var command = CreateCommand(rawData);
        var now = DateTimeOffset.Parse("2026-07-10T22:00:00Z");

        var path = WatchdogRepairRequestWriter.Queue(
            Path.Combine(_root, RemoteRepairContract.RequestFileName),
            command,
            rawData,
            now);

        Assert.True(RemoteRepairContract.TryDeserialize(
            File.ReadAllText(path),
            out var request,
            out var code), code);
        Assert.Equal(rawData, request!.DataJson);
        Assert.Equal(command.Command, request.Command);
        Assert.Equal(command.AgentId, request.AgentId);
        Assert.Equal(command.MachineFingerprint, request.MachineFingerprint);
        Assert.Equal(command.Timestamp, request.Timestamp);
        Assert.Equal(command.Nonce, request.Nonce);
        Assert.Equal(command.KeyId, request.KeyId);
        Assert.Equal(command.Signature, request.Signature);
        Assert.Equal(command.DataHash, request.DataHash);
        Assert.Equal("cmd-exact-1", request.CommandId);
        Assert.Equal("watchdog_critical", request.Reason);
        Assert.Equal(now.ToString("O"), request.RequestedAtUtc);
    }

    [Fact]
    public void Queue_rejects_data_that_no_longer_matches_signed_hash()
    {
        const string signedData = "{\"commandId\":\"cmd-1\",\"reason\":\"watchdog_critical\",\"expiresAt\":\"2026-07-10T22:04:00.0000000+00:00\"}";
        var command = CreateCommand(signedData);

        Assert.Throws<InvalidDataException>(() => WatchdogRepairRequestWriter.Queue(
            Path.Combine(_root, RemoteRepairContract.RequestFileName),
            command,
            "{\"commandId\":\"cmd-2\",\"reason\":\"watchdog_critical\",\"expiresAt\":\"2026-07-10T22:04:00.0000000+00:00\"}"));
    }

    [Fact]
    public void Queue_rejects_non_minimum_necessary_signed_data()
    {
        const string data =
            "{\"commandId\":\"cmd-1\",\"reason\":\"watchdog_critical\",\"expiresAt\":\"2026-07-10T22:04:00.0000000+00:00\",\"patientName\":\"forbidden\"}";
        var command = CreateCommand(data);

        Assert.Throws<InvalidDataException>(() => WatchdogRepairRequestWriter.Queue(
            Path.Combine(_root, RemoteRepairContract.RequestFileName),
            command,
            data));
    }

    [Fact]
    public void Queue_atomically_replaces_prior_request_without_temp_residue()
    {
        var path = Path.Combine(_root, RemoteRepairContract.RequestFileName);
        const string first = "{\"commandId\":\"cmd-1\",\"reason\":\"remote_command\",\"expiresAt\":\"2026-07-10T22:04:00.0000000+00:00\"}";
        const string second = "{\"commandId\":\"cmd-2\",\"reason\":\"operator_requested\",\"expiresAt\":\"2026-07-10T22:04:00.0000000+00:00\"}";
        WatchdogRepairRequestWriter.Queue(path, CreateCommand(first), first);
        WatchdogRepairRequestWriter.Queue(path, CreateCommand(second), second);

        Assert.True(RemoteRepairContract.TryDeserialize(
            File.ReadAllText(path),
            out var request,
            out _));
        Assert.Equal("cmd-2", request!.CommandId);
        Assert.Empty(Directory.EnumerateFiles(_root, "*.tmp", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void Queue_rejects_request_file_reparse_without_following_target()
    {
        if (OperatingSystem.IsWindows())
            return;

        Directory.CreateDirectory(_root);
        var requestPath = Path.Combine(_root, RemoteRepairContract.RequestFileName);
        var targetPath = Path.Combine(_root, "target.json");
        File.WriteAllText(targetPath, "target-must-not-change");
        File.CreateSymbolicLink(requestPath, targetPath);
        const string data = "{\"commandId\":\"cmd-1\",\"reason\":\"remote_command\",\"expiresAt\":\"2026-07-10T22:04:00.0000000+00:00\"}";

        Assert.Throws<InvalidDataException>(() => WatchdogRepairRequestWriter.Queue(
            requestPath,
            CreateCommand(data),
            data));
        Assert.Equal("target-must-not-change", File.ReadAllText(targetPath));
        Assert.NotNull(new FileInfo(requestPath).LinkTarget);
    }

    [Fact]
    public void Queue_rejects_reparse_parent_without_writing_through_it()
    {
        if (OperatingSystem.IsWindows())
            return;

        Directory.CreateDirectory(_root);
        var targetDirectory = Path.Combine(_root, "target-dir");
        var linkedDirectory = Path.Combine(_root, "linked-dir");
        Directory.CreateDirectory(targetDirectory);
        Directory.CreateSymbolicLink(linkedDirectory, targetDirectory);
        const string data = "{\"commandId\":\"cmd-1\",\"reason\":\"remote_command\",\"expiresAt\":\"2026-07-10T22:04:00.0000000+00:00\"}";

        Assert.Throws<InvalidDataException>(() => WatchdogRepairRequestWriter.Queue(
            Path.Combine(linkedDirectory, RemoteRepairContract.RequestFileName),
            CreateCommand(data),
            data));
        Assert.Empty(Directory.EnumerateFileSystemEntries(targetDirectory));
    }

    private static SignedCommand CreateCommand(string data) => new(
        Command: "repair_agent",
        AgentId: "agent-writer-test",
        MachineFingerprint: "fingerprint-writer-test",
        Timestamp: "2026-07-10T22:00:00.0000000+00:00",
        Nonce: Guid.NewGuid().ToString("N"),
        KeyId: "test-key",
        Signature: Convert.ToBase64String(new byte[64]),
        DataHash: RemoteCommandTrust.ComputeSha256Hex(data),
        ExpiresAt: Expiry);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
