using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Core.Reasoning;
using Xunit;

namespace SuavoAgent.Core.Tests.Reasoning;

public class YamlRuleLoaderFingerprintTests
{
    private static YamlRuleLoader Loader() => new(NullLogger<YamlRuleLoader>.Instance);

    [Fact]
    public void ParseYaml_ElementFingerprintsAbsent_EmptyList()
    {
        const string yaml = @"
rules:
  - id: legacy.rule
    skillId: test
    when:
      processName: Foo*
    then:
      - type: Log
";
        var rules = Loader().ParseYaml(yaml);
        Assert.Single(rules);
        Assert.Empty(rules[0].When.ElementFingerprints);
    }

    [Fact]
    public void ParseYaml_ElementFingerprintsPresent_Parsed()
    {
        const string yaml = @"
rules:
  - id: fp.rule
    skillId: test
    when:
      processName: PioneerPharmacy*
      elementFingerprints:
        - controlType: Button
          automationId: btnApprove
          className: WinForms.Button
        - controlType: Edit
          automationId: txtRxNumber
    then:
      - type: Log
";
        var rules = Loader().ParseYaml(yaml);
        Assert.Single(rules);
        var fps = rules[0].When.ElementFingerprints;
        Assert.Equal(2, fps.Count);
        Assert.Equal("Button", fps[0].ControlType);
        Assert.Equal("btnApprove", fps[0].AutomationId);
        Assert.Equal("WinForms.Button", fps[0].ClassName);
        Assert.Equal("Edit", fps[1].ControlType);
        Assert.Equal("txtRxNumber", fps[1].AutomationId);
        Assert.Null(fps[1].ClassName);
    }

    [Fact]
    public void ParseYaml_FingerprintMissingControlType_Throws()
    {
        const string yaml = @"
rules:
  - id: bad.rule
    skillId: test
    when:
      elementFingerprints:
        - automationId: btnApprove
    then:
      - type: Log
";
        Assert.Throws<System.InvalidOperationException>(() => Loader().ParseYaml(yaml));
    }

    [Fact]
    public void ParseYaml_FingerprintMissingAutomationId_Throws()
    {
        const string yaml = @"
rules:
  - id: bad.rule
    skillId: test
    when:
      elementFingerprints:
        - controlType: Button
    then:
      - type: Log
";
        Assert.Throws<System.InvalidOperationException>(() => Loader().ParseYaml(yaml));
    }

    [Fact]
    public void ParseYaml_VerifyAfter_CanCarryFingerprints()
    {
        const string yaml = @"
rules:
  - id: va.rule
    skillId: test
    when:
      processName: Foo*
    then:
      - type: Click
        parameters: { automationId: btnApprove }
        verifyAfter:
          elementFingerprints:
            - controlType: Window
              automationId: wndApproved
              className: WinForms.Form
";
        var rules = Loader().ParseYaml(yaml);
        Assert.Single(rules);
        var va = rules[0].Then[0].VerifyAfter;
        Assert.NotNull(va);
        Assert.Single(va!.ElementFingerprints);
        Assert.Equal("wndApproved", va.ElementFingerprints[0].AutomationId);
    }
}
