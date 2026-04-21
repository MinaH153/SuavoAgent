using System.Text.Json;
using SuavoAgent.Setup.Gui.Services;
using Xunit;

namespace SuavoAgent.Setup.Tests;

/// <summary>
/// ConsentReceiptData serializes to the schema HeartbeatWorker uploads on first
/// heartbeat. Any drift between these tests and the upload contract breaks
/// cloud intake for every new pharmacy. Keep the field list in lockstep with
/// <c>bootstrap.ps1</c>'s consentReceipt hashtable.
/// </summary>
public sealed class ConsentReceiptDataTests
{
    [Fact]
    public void ToJson_emits_expected_top_level_fields()
    {
        var data = new ConsentReceiptData(
            AuthorizingName: "Jane Doe",
            AuthorizingTitle: "Pharmacy Owner",
            BusinessState: "ca",
            MandatoryNoticeState: false,
            EmployeeNoticeAcknowledged: true,
            Timestamp: new DateTimeOffset(2026, 4, 21, 12, 0, 0, TimeSpan.Zero));

        var json = data.ToJson(
            pharmacyId: "PH123",
            agentId: "agent-abc123",
            installerVersion: "3.13.6",
            machineFingerprint: "guid-xyz");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("1.0", root.GetProperty("consentVersion").GetString());
        Assert.Equal("CA", root.GetProperty("businessState").GetString());
        Assert.False(root.GetProperty("mandatoryNoticeState").GetBoolean());
        Assert.True(root.GetProperty("termsAccepted").GetBoolean());
        Assert.True(root.GetProperty("employeeNoticeAcknowledged").GetBoolean());
        Assert.Equal("3.13.6", root.GetProperty("installerVersion").GetString());
        Assert.Equal("guid-xyz", root.GetProperty("machineFingerprint").GetString());
        Assert.Equal("PH123", root.GetProperty("pharmacyId").GetString());
        Assert.Equal("agent-abc123", root.GetProperty("agentId").GetString());
        Assert.Equal("gui_installer", root.GetProperty("source").GetString());

        var authorizing = root.GetProperty("authorizingParty");
        Assert.Equal("Jane Doe", authorizing.GetProperty("name").GetString());
        Assert.Equal("Pharmacy Owner", authorizing.GetProperty("title").GetString());
    }

    [Fact]
    public void ToJson_uppercases_state_even_when_input_mixed_case()
    {
        var data = NewFrom(state: "ny", mandatory: true);
        var json = data.ToJson("PH", "AG", "0.0.0", "fp");
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("NY", doc.RootElement.GetProperty("businessState").GetString());
    }

    [Theory]
    [InlineData("CT", true)]
    [InlineData("DE", true)]
    [InlineData("NY", true)]
    [InlineData("ct", true)]
    [InlineData("  ny  ", true)]
    [InlineData("CA", false)]
    [InlineData("TX", false)]
    [InlineData("", false)]
    public void RequiresMandatoryNotice_matches_bootstrap_spec(string state, bool expected)
        => Assert.Equal(expected, ConsentReceiptData.RequiresMandatoryNotice(state));

    [Theory]
    [InlineData("CA", true)]
    [InlineData("IL", true)]
    [InlineData("MA", true)]
    [InlineData("MD", true)]
    [InlineData("CO", true)]
    [InlineData("MT", true)]
    [InlineData("NY", false)]
    [InlineData("TX", false)]
    public void IsHighRisk_matches_bootstrap_spec(string state, bool expected)
        => Assert.Equal(expected, ConsentReceiptData.IsHighRisk(state));

    [Fact]
    public void Timestamp_serializes_as_iso8601_round_trip()
    {
        var ts = new DateTimeOffset(2026, 4, 21, 7, 30, 15, TimeSpan.FromHours(-7));
        var data = NewFrom(ts: ts);
        var json = data.ToJson("PH", "AG", "0.0.0", "fp");
        using var doc = JsonDocument.Parse(json);

        var parsed = DateTimeOffset.Parse(doc.RootElement.GetProperty("consentTimestamp").GetString()!);
        Assert.Equal(ts.ToUniversalTime(), parsed.ToUniversalTime());
    }

    private static ConsentReceiptData NewFrom(
        string state = "CA",
        bool mandatory = false,
        DateTimeOffset? ts = null)
        => new(
            AuthorizingName: "Jane",
            AuthorizingTitle: "Owner",
            BusinessState: state,
            MandatoryNoticeState: mandatory,
            EmployeeNoticeAcknowledged: true,
            Timestamp: ts ?? DateTimeOffset.UtcNow);
}
