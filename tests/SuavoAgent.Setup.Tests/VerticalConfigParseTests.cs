using System.Text.Json;
using System.Text.Json.Nodes;
using SuavoAgent.Setup;
using Xunit;

namespace SuavoAgent.Setup.Tests;

public class VerticalConfigParseTests
{
    [Fact]
    public void Parses_full_pharmacy_config()
    {
        var json = """{"vertical":"pharmacy","complianceMode":"hipaa","systemConnector":"pioneerrx","connectorLabel":"PioneerRx","redactionProfileId":"phi-v1","framing":{"productNoun":"SuavoAgent","systemNoun":"PioneerRx","businessNoun":"pharmacy","idLabel":"NPI"},"compliance":{"baaRequired":true,"consentCopyId":"hipaa-ba-v1"}}""";
        var dto = JsonSerializer.Deserialize<VerticalConfigDto>(json);
        Assert.NotNull(dto);
        Assert.Equal("hipaa", dto!.ComplianceMode);
        Assert.Equal("pioneerrx", dto.SystemConnector);
        Assert.True(dto.IsValid);
    }

    // (a) absent — field not present in the response → Raw == null
    [Fact]
    public void Absent_field_yields_null_raw()
    {
        var data = JsonNode.Parse("""{"apiKey":"k"}""")!.AsObject();
        var p = VerticalConfigPayloadParser.Parse(data);
        Assert.Null(p.Raw);
        Assert.Null(p.Dto);
    }

    // (c) present but malformed → Raw != null, Dto == null, must not throw
    [Fact]
    public void Malformed_field_keeps_raw_but_null_dto_never_throws()
    {
        var data = JsonNode.Parse("""{"verticalConfig":"not-an-object","verticalConfigSignature":"sig"}""")!.AsObject();
        var p = VerticalConfigPayloadParser.Parse(data);
        Assert.NotNull(p.Raw);
        Assert.Null(p.Dto);
        Assert.Equal("sig", p.Signature);
    }

    // valid → DTO parsed + sig + keyId carried
    [Fact]
    public void Valid_field_parses_dto_and_carries_sig_and_keyid()
    {
        var data = JsonNode.Parse("""{"verticalConfig":{"vertical":"default","complianceMode":"none","systemConnector":"none","connectorLabel":"your system","redactionProfileId":"none","framing":{"productNoun":"SuavoAgent","systemNoun":"your system","businessNoun":"business","idLabel":"License ID"},"compliance":{"baaRequired":false,"consentCopyId":"terms-v1"}},"verticalConfigSignature":"sig","verticalConfigKeyId":"vertical-v1"}""")!.AsObject();
        var p = VerticalConfigPayloadParser.Parse(data);
        Assert.NotNull(p.Dto);
        Assert.Equal("none", p.Dto!.ComplianceMode);
        Assert.Equal("vertical-v1", p.KeyId);
    }

    [Fact]
    public void ParseVerticalConfigFromData_null_data_returns_all_null()
    {
        var p = VerticalConfigPayloadParser.Parse(null);
        Assert.Null(p.Raw);
        Assert.Null(p.Dto);
        Assert.Null(p.Signature);
        Assert.Null(p.KeyId);
    }
}
