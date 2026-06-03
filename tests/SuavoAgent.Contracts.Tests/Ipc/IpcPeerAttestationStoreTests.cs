using SuavoAgent.Contracts.Ipc;
using Xunit;

namespace SuavoAgent.Contracts.Tests.Ipc;

public sealed class IpcPeerAttestationStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"suavo-ipc-attest-{Guid.NewGuid():N}");
    private readonly string _path;

    public IpcPeerAttestationStoreTests()
    {
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "helper-attestations.json");
    }

    [Fact]
    public void WriteAndContainsHelper_BindsPidToPipeNonce()
    {
        IpcPeerAttestationStore.Write(
            _path,
            pipeNonce: "abc123",
            helpers:
            [
                new IpcPeerAttestationEntry(
                    ProcessId: 15512,
                    SessionId: 1,
                    LaunchedAt: DateTimeOffset.UtcNow,
                    HelperSha256: "abc")
            ],
            now: DateTimeOffset.UtcNow);

        Assert.True(IpcPeerAttestationStore.ContainsHelper(
            _path,
            pipeNonce: "abc123",
            processId: 15512,
            now: DateTimeOffset.UtcNow,
            maxAge: TimeSpan.FromDays(7)));

        Assert.False(IpcPeerAttestationStore.ContainsHelper(
            _path,
            pipeNonce: "other",
            processId: 15512,
            now: DateTimeOffset.UtcNow,
            maxAge: TimeSpan.FromDays(7)));
    }

    [Fact]
    public void ContainsHelper_RejectsStaleAttestation()
    {
        var writtenAt = DateTimeOffset.UtcNow.AddDays(-8);
        IpcPeerAttestationStore.Write(
            _path,
            pipeNonce: "abc123",
            helpers:
            [
                new IpcPeerAttestationEntry(
                    ProcessId: 15512,
                    SessionId: 1,
                    LaunchedAt: writtenAt,
                    HelperSha256: "abc")
            ],
            now: writtenAt);

        Assert.False(IpcPeerAttestationStore.ContainsHelper(
            _path,
            pipeNonce: "abc123",
            processId: 15512,
            now: DateTimeOffset.UtcNow,
            maxAge: TimeSpan.FromDays(7)));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
