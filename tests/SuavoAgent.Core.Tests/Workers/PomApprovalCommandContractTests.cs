using System.Text.Json;
using SuavoAgent.Core.Workers;
using Xunit;

namespace SuavoAgent.Core.Tests.Workers;

public sealed class PomApprovalCommandContractTests
{
    private const string CommandId = "11111111-1111-4111-8111-111111111111";
    private const string PomId = "22222222-2222-4222-8222-222222222222";
    private const string ApprovedBy = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
    private const string SessionId = "learn-44444444-4444-4444-8444-444444444444-20260710120000";

    [Fact]
    public void ExactSchema_ParsesAndBindsLowercaseDigests()
    {
        var data = Build();

        Assert.True(PomApprovalCommandContract.TryParse(data, out var command, out var error));
        Assert.Equal("", error);
        Assert.NotNull(command);
        Assert.Equal(1, command!.SchemaVersion);
        Assert.Equal(CommandId, command.CommandId);
        Assert.Equal(PomId, command.PomId);
        Assert.Equal(SessionId, command.SessionId);
        Assert.Equal("a".PadLeft(64, 'a'), command.ApprovedModelDigest);
        Assert.Equal("b".PadLeft(64, 'b'), command.ApprovedTemplateDigest);
        Assert.Equal(ApprovedBy, command.ApprovedBy);
        Assert.Equal(ExpiresAt, command.ExpiresAt);
        Assert.Equal(PomApprovalCommandContract.ComputePayloadDigest(data), command.PayloadDigest);
    }

    [Theory]
    [InlineData("schemaVersion")]
    [InlineData("commandId")]
    [InlineData("pomId")]
    [InlineData("sessionId")]
    [InlineData("approvedModelDigest")]
    [InlineData("approvedTemplateDigest")]
    [InlineData("approvedBy")]
    [InlineData("expiresAt")]
    public void MissingRequiredField_Rejects(string field)
    {
        var values = Fields();
        values.Remove(field);
        var data = JsonSerializer.SerializeToElement(values);

        Assert.False(PomApprovalCommandContract.TryParse(data, out _, out var error));
        Assert.Equal("pom_approval_schema_invalid", error);
    }

    [Fact]
    public void ExtraOrDuplicateField_Rejects()
    {
        var values = Fields();
        values["extra"] = true;
        Assert.False(PomApprovalCommandContract.TryParse(
            JsonSerializer.SerializeToElement(values), out _, out _));

        using var duplicate = JsonDocument.Parse(
            $$"""{"schemaVersion":1,"pomId":"{{PomId}}","sessionId":"{{SessionId}}","approvedModelDigest":"{{new string('a', 64)}}","approvedTemplateDigest":"{{new string('b', 64)}}","approvedBy":"{{ApprovedBy}}","expiresAt":"2026-07-10T12:15:00.000Z","commandId":"{{CommandId}}","commandId":"{{CommandId}}"}""");
        Assert.False(PomApprovalCommandContract.TryParse(duplicate.RootElement, out _, out _));
    }

    [Fact]
    public void UppercaseDigestOrUuidAndUnsafeSession_Reject()
    {
        var uppercaseDigest = Fields();
        uppercaseDigest["approvedModelDigest"] = new string('A', 64);
        Assert.False(PomApprovalCommandContract.TryParse(
            JsonSerializer.SerializeToElement(uppercaseDigest), out _, out _));

        var uppercaseUuid = Fields();
        uppercaseUuid["approvedBy"] = ApprovedBy.ToUpperInvariant();
        Assert.False(PomApprovalCommandContract.TryParse(
            JsonSerializer.SerializeToElement(uppercaseUuid), out _, out _));

        var unsafeSession = Fields();
        unsafeSession["sessionId"] = "learn-session\nforged";
        Assert.False(PomApprovalCommandContract.TryParse(
            JsonSerializer.SerializeToElement(unsafeSession), out _, out _));
    }

    [Fact]
    public void PayloadDigest_BindsExactJsonBytes()
    {
        var first = Build();
        var secondValues = Fields();
        secondValues["approvedTemplateDigest"] = new string('c', 64);
        var second = JsonSerializer.SerializeToElement(secondValues);

        Assert.NotEqual(
            PomApprovalCommandContract.ComputePayloadDigest(first),
            PomApprovalCommandContract.ComputePayloadDigest(second));
    }

    [Fact]
    public void MalformedSchema_WithCanonicalCommandIdStillHasDurableIdentity()
    {
        var values = Fields();
        values.Remove("approvedBy");
        var data = JsonSerializer.SerializeToElement(values);

        Assert.True(PomApprovalCommandContract.TryGetLedgerIdentity(
            data, out var commandId, out var digest));
        Assert.Equal(CommandId, commandId);
        Assert.Matches("^[a-f0-9]{64}$", digest);
    }

    private static JsonElement Build() => JsonSerializer.SerializeToElement(Fields());

    private static Dictionary<string, object> Fields() => new()
    {
        ["schemaVersion"] = 1,
        ["pomId"] = PomId,
        ["sessionId"] = SessionId,
        ["approvedModelDigest"] = new string('a', 64),
        ["approvedTemplateDigest"] = new string('b', 64),
        ["approvedBy"] = ApprovedBy,
        ["commandId"] = CommandId,
        ["expiresAt"] = ExpiresAt.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
    };

    private static readonly DateTimeOffset ExpiresAt =
        new(2026, 7, 10, 12, 15, 0, TimeSpan.Zero);
}
