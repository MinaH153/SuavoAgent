using System.Text.Json;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Contracts.Security;
using SuavoAgent.Contracts.Vision;
using SuavoAgent.Core.Vision;
using SuavoAgent.Helper.Vision;
using Xunit;

namespace SuavoAgent.Helper.Tests.Vision;

public sealed class VisionGenerationGateTests
{
    private const string CommandId = "11111111-1111-4111-8111-111111111111";
    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), "suavo-vision-gate");

    [Fact]
    public void Gate_starts_fail_closed_until_authenticated_handshake()
    {
        var gate = CreateGate(generation: 4);

        Assert.False(gate.IsMatched);
    }

    [Fact]
    public void Exact_generation_and_digest_latch_gate()
    {
        var gate = CreateGate(generation: 4);
        var data = JsonSerializer.SerializeToElement(new VisionStateHandshake(
            VisionStateHandshake.CurrentSchemaVersion,
            gate.LocalGeneration,
            gate.LocalDigest));

        var result = gate.VerifyAndLatch(data);

        Assert.True(result.Accepted, result.Code);
        Assert.True(gate.IsMatched);
    }

    [Fact]
    public void New_connection_reset_revokes_an_older_successful_proof()
    {
        var gate = CreateGate(generation: 4);
        var data = JsonSerializer.SerializeToElement(new VisionStateHandshake(
            VisionStateHandshake.CurrentSchemaVersion,
            gate.LocalGeneration,
            gate.LocalDigest));
        Assert.True(gate.VerifyAndLatch(data).Accepted);

        gate.Reset();

        Assert.False(gate.IsMatched);
    }

    [Fact]
    public void Helper_only_restart_on_staged_generation_rejects_old_Core()
    {
        var helperGate = CreateGate(generation: 5);
        var oldCore = JsonSerializer.SerializeToElement(new VisionStateHandshake(
            VisionStateHandshake.CurrentSchemaVersion,
            4,
            helperGate.LocalDigest));

        var result = helperGate.VerifyAndLatch(oldCore);

        Assert.False(result.Accepted);
        Assert.Equal("vision_generation_mismatch", result.Code);
        Assert.False(helperGate.IsMatched);
    }

    [Fact]
    public void A_later_mismatch_unlatches_a_previously_matching_connection()
    {
        var gate = CreateGate(generation: 2);
        var exact = JsonSerializer.SerializeToElement(new VisionStateHandshake(
            1,
            2,
            gate.LocalDigest));
        Assert.True(gate.VerifyAndLatch(exact).Accepted);
        var mismatch = JsonSerializer.SerializeToElement(new VisionStateHandshake(
            1,
            3,
            gate.LocalDigest));

        Assert.False(gate.VerifyAndLatch(mismatch).Accepted);
        Assert.False(gate.IsMatched);
    }

    [Theory]
    [InlineData("{}", "vision_handshake_fields_invalid")]
    [InlineData("{\"SchemaVersion\":1,\"generation\":1,\"configDigest\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"}", "vision_handshake_fields_invalid")]
    [InlineData("{\"schemaVersion\":1,\"generation\":1,\"generation\":1,\"configDigest\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"}", "vision_handshake_duplicate_field")]
    public void Malformed_handshake_never_latches(string json, string code)
    {
        var gate = CreateGate(generation: 1);
        using var document = JsonDocument.Parse(json);

        var result = gate.VerifyAndLatch(document.RootElement);

        Assert.False(result.Accepted);
        Assert.Equal(code, result.Code);
        Assert.False(gate.IsMatched);
    }

    private VisionGenerationGate CreateGate(long generation)
    {
        var options = VisionOptionsSnapshot.DisabledDefault();
        var state = VisionConfigurationStateCodec.Create(
            generation,
            CommandId,
            new DateTimeOffset(2026, 7, 11, 0, 0, 0, TimeSpan.Zero),
            options,
            _dataDirectory);
        var json = VisionConfigurationStateCodec.Serialize(state, _dataDirectory);
        var loaded = VisionConfigurationRegistry.Load(
            new StubStore(json),
            _dataDirectory);
        return new VisionGenerationGate(loaded);
    }

    private sealed class StubStore(string value) : IVisionConfigurationStore
    {
        public VisionRegistryReadResult Read() => new(
            VisionRegistryReadStatus.Present,
            "present",
            value);
        public void Write(string next) => throw new NotSupportedException();
    }
}
