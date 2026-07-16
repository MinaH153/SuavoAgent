using SuavoAgent.Contracts.Security;
using SuavoAgent.Contracts.Vision;
using SuavoAgent.Core.Vision;
using Xunit;

namespace SuavoAgent.Core.Tests.Vision;

public sealed class VisionConfigurationStateTests
{
    private const string CommandOne = "11111111-1111-4111-8111-111111111111";
    private const string CommandTwo = "22222222-2222-4222-8222-222222222222";
    private readonly string _dataDirectory = Path.Combine(
        Path.GetTempPath(),
        "suavo-vision-state");
    private static readonly DateTimeOffset AppliedAt = new(
        2026, 7, 11, 12, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Strict_state_round_trips_deterministically()
    {
        var options = VisionOptionsSnapshot.DisabledDefault();
        var state = VisionConfigurationStateCodec.Create(
            7,
            CommandOne,
            AppliedAt,
            options,
            _dataDirectory);

        var first = VisionConfigurationStateCodec.Serialize(state, _dataDirectory);
        var second = VisionConfigurationStateCodec.Serialize(state, _dataDirectory);
        var parsed = VisionConfigurationStateCodec.Parse(first, _dataDirectory);

        Assert.Equal(first, second);
        Assert.True(parsed.IsValid, parsed.Code);
        Assert.Equal(state, parsed.State);
        Assert.Equal(
            VisionConfigurationStateCodec.ComputeConfigDigest(options),
            state.ConfigDigest);
    }

    [Theory]
    [InlineData("\"schemaVersion\"", "\"SchemaVersion\"", "vision_state_unknown_field")]
    [InlineData("\"configDigest\"", "\"extra\":1,\"configDigest\"", "vision_state_unknown_field")]
    [InlineData("\"retentionHours\"", "\"RetentionHours\"", "vision_state_options_unknown_field")]
    public void Unknown_or_wrong_case_fields_are_rejected(
        string original,
        string replacement,
        string expectedCode)
    {
        var json = ValidJson().Replace(original, replacement, StringComparison.Ordinal);

        var parsed = VisionConfigurationStateCodec.Parse(json, _dataDirectory);

        Assert.False(parsed.IsValid);
        Assert.Equal(expectedCode, parsed.Code);
    }

    [Fact]
    public void Duplicate_fields_are_rejected()
    {
        var json = ValidJson().Replace(
            "\"generation\":1",
            "\"generation\":1,\"generation\":1",
            StringComparison.Ordinal);

        var parsed = VisionConfigurationStateCodec.Parse(json, _dataDirectory);

        Assert.False(parsed.IsValid);
        Assert.Equal("vision_state_duplicate_field", parsed.Code);
    }

    [Fact]
    public void Missing_required_fields_are_rejected()
    {
        var json = ValidJson().Replace("\"schemaVersion\":1,", "", StringComparison.Ordinal);

        var parsed = VisionConfigurationStateCodec.Parse(json, _dataDirectory);

        Assert.False(parsed.IsValid);
        Assert.Equal("vision_state_missing_field", parsed.Code);
    }

    [Theory]
    [InlineData("{}", "vision_state_missing_field")]
    [InlineData("not-json", "vision_state_parse_failed_JsonReaderException")]
    [InlineData("", "vision_state_empty")]
    public void Malformed_state_never_becomes_disabled_default(string json, string expectedCode)
    {
        var parsed = VisionConfigurationStateCodec.Parse(json, _dataDirectory);

        Assert.False(parsed.IsValid);
        Assert.Equal(expectedCode, parsed.Code);
    }

    [Fact]
    public void Digest_mismatch_is_rejected()
    {
        var json = ValidJson().Replace(
            "\"visionOptions\":{\"enabled\":false",
            "\"visionOptions\":{\"enabled\":true",
            StringComparison.Ordinal);

        var parsed = VisionConfigurationStateCodec.Parse(json, _dataDirectory);

        Assert.False(parsed.IsValid);
        Assert.Equal("vision_state_digest_mismatch", parsed.Code);
    }

    [Fact]
    public void Invalid_generation_is_rejected()
    {
        var json = ValidJson().Replace(
            "\"generation\":1",
            "\"generation\":0",
            StringComparison.Ordinal);

        var parsed = VisionConfigurationStateCodec.Parse(json, _dataDirectory);

        Assert.False(parsed.IsValid);
        Assert.Equal("vision_state_generation_invalid", parsed.Code);
    }

    [Fact]
    public void Invalid_option_bounds_are_rejected_even_with_matching_digest()
    {
        var invalid = VisionOptionsSnapshot.DisabledDefault() with { RetentionHours = 169 };

        var exception = Assert.Throws<ArgumentException>(() =>
            VisionConfigurationStateCodec.Create(
                1,
                CommandOne,
                AppliedAt,
                invalid,
                _dataDirectory));

        Assert.Contains("vision_state_retention_hours_invalid", exception.Message);
    }

    [Fact]
    public void Disabled_master_rejects_enabled_subfeatures()
    {
        var defaults = VisionOptionsSnapshot.DisabledDefault();
        var invalid = defaults with
        {
            PeriodicCapture = defaults.PeriodicCapture with { Enabled = true },
        };

        var exception = Assert.Throws<ArgumentException>(() =>
            VisionConfigurationStateCodec.Create(
                1,
                CommandOne,
                AppliedAt,
                invalid,
                _dataDirectory));

        Assert.Contains("vision_state_disabled_subfeature_enabled", exception.Message);
    }

    [Fact]
    public void Missing_state_is_explicit_default_disabled()
    {
        var store = new MemoryStore();

        var loaded = VisionConfigurationRegistry.Load(store, _dataDirectory);

        Assert.True(loaded.IsValid);
        Assert.True(loaded.IsMissing);
        Assert.Equal(0, loaded.EffectiveGeneration);
        Assert.False(loaded.EffectiveOptions.Enabled);
        Assert.Equal("vision_registry_state_missing", loaded.Code);
    }

    [Fact]
    public void Coordinator_persists_explicit_disabled_and_increments_generation()
    {
        var store = new MemoryStore();
        var coordinator = new VisionConfigurationCoordinator(
            store,
            _dataDirectory,
            () => AppliedAt);
        var disabled = VisionOptionsSnapshot.DisabledDefault();

        var first = coordinator.Apply(CommandOne, disabled);
        var second = coordinator.Apply(CommandTwo, disabled);

        Assert.True(first.Succeeded, first.Code);
        Assert.False(first.IdempotentReplay);
        Assert.Equal(1, first.State?.Generation);
        Assert.False(first.State!.VisionOptions.Enabled);
        Assert.True(second.Succeeded, second.Code);
        Assert.Equal(2, second.State?.Generation);
        Assert.Equal(2, store.WriteCount);
    }

    [Fact]
    public void Same_command_and_digest_is_idempotent_without_rewrite()
    {
        var store = new MemoryStore();
        var coordinator = new VisionConfigurationCoordinator(
            store,
            _dataDirectory,
            () => AppliedAt);
        var options = VisionOptionsSnapshot.DisabledDefault();

        var first = coordinator.Apply(CommandOne, options);
        var replay = coordinator.Apply(CommandOne, options);

        Assert.True(first.Succeeded, first.Code);
        Assert.True(replay.Succeeded, replay.Code);
        Assert.True(replay.IdempotentReplay);
        Assert.Equal(1, replay.State?.Generation);
        Assert.Equal(1, store.WriteCount);
    }

    [Fact]
    public void Same_command_with_different_digest_is_rejected()
    {
        var store = new MemoryStore();
        var coordinator = new VisionConfigurationCoordinator(
            store,
            _dataDirectory,
            () => AppliedAt);
        var original = VisionOptionsSnapshot.DisabledDefault();
        var changed = original with { RetentionHours = 12 };

        Assert.True(coordinator.Apply(CommandOne, original).Succeeded);
        var conflict = coordinator.Apply(CommandOne, changed);

        Assert.False(conflict.Succeeded);
        Assert.Equal("vision_command_replay_conflict", conflict.Code);
        Assert.Equal(1, store.WriteCount);
    }

    [Fact]
    public void Invalid_existing_registry_state_blocks_new_writes()
    {
        var store = new MemoryStore("{}");
        var coordinator = new VisionConfigurationCoordinator(
            store,
            _dataDirectory,
            () => AppliedAt);

        var result = coordinator.Apply(CommandOne, VisionOptionsSnapshot.DisabledDefault());

        Assert.False(result.Succeeded);
        Assert.Equal("vision_state_missing_field", result.Code);
        Assert.Equal(0, store.WriteCount);
    }

    [Fact]
    public void Telemetry_distinguishes_effective_from_staged_generation()
    {
        var store = new MemoryStore();
        var effective = VisionConfigurationRegistry.Load(store, _dataDirectory);
        var status = new VisionConfigurationStatusProvider(
            effective,
            store,
            _dataDirectory);
        var coordinator = new VisionConfigurationCoordinator(
            store,
            _dataDirectory,
            () => AppliedAt);

        Assert.True(coordinator.Apply(
            CommandOne,
            VisionOptionsSnapshot.DisabledDefault()).Succeeded);
        status.RecordStructuralFailure("vision_command_id_invalid");
        var telemetry = status.Snapshot();

        Assert.Equal("restart_required", telemetry.Status);
        Assert.Equal(0, telemetry.EffectiveGeneration);
        Assert.Equal(1, telemetry.StagedGeneration);
        Assert.Equal("vision_command_id_invalid", telemetry.LastStructuralFailure);
        Assert.NotNull(telemetry.LastStructuralFailureAt);
    }

    [Fact]
    public void Configuration_projection_targets_nested_Agent_Vision_section()
    {
        var values = VisionOptionsSnapshot.DisabledDefault().ToConfigurationValues();

        Assert.Contains("Agent:Vision:Enabled", values.Keys);
        Assert.Contains("Agent:Vision:Tesseract:Enabled", values.Keys);
        Assert.DoesNotContain("Vision:Enabled", values.Keys);
    }

    private string ValidJson()
    {
        var state = VisionConfigurationStateCodec.Create(
            1,
            CommandOne,
            AppliedAt,
            VisionOptionsSnapshot.DisabledDefault(),
            _dataDirectory);
        return VisionConfigurationStateCodec.Serialize(state, _dataDirectory);
    }

    private sealed class MemoryStore : IVisionConfigurationStore
    {
        private string? _value;

        public MemoryStore(string? value = null) => _value = value;

        public int WriteCount { get; private set; }

        public VisionRegistryReadResult Read() => _value is null
            ? new(VisionRegistryReadStatus.Missing, "vision_registry_state_missing")
            : new(VisionRegistryReadStatus.Present, "present", _value);

        public void Write(string value)
        {
            WriteCount++;
            _value = value;
        }
    }
}
