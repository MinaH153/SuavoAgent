using SuavoAgent.Core.Intelligence;
using Xunit;

namespace SuavoAgent.Core.Tests.Intelligence;

public class ComplianceBoundaryTests
{
    [Fact]
    public void Validate_CleanJson_ReturnsClean()
    {
        var json = """{"industry":"pharmacy","processorCount":4,"appName":"EXCEL.EXE"}""";
        var (isClean, violations) = ComplianceBoundary.Validate(json);
        Assert.True(isClean, string.Join("; ", violations));
    }

    [Fact]
    public void Validate_WithSSN_Rejects()
    {
        var json = """{"data":"SSN is 123-45-6789"}""";
        var (isClean, _) = ComplianceBoundary.Validate(json);
        Assert.False(isClean);
    }

    [Fact]
    public void Validate_WithEmail_Rejects()
    {
        var json = """{"contact":"john.doe@email.com"}""";
        var (isClean, _) = ComplianceBoundary.Validate(json);
        Assert.False(isClean);
    }

    [Fact]
    public void ValidateFields_HashedFieldsPass()
    {
        var fields = new Dictionary<string, object?>
        {
            ["windowTitleHash"] = "a3f2b1c4d5e6f7a8b9c0d1e2f3a4b5c6",
            ["appName"] = "EXCEL.EXE"
        };
        var (isClean, _) = ComplianceBoundary.ValidateFields(fields);
        Assert.True(isClean);
    }

    [Fact]
    public void ValidateFields_UnhashedField_Rejects()
    {
        var fields = new Dictionary<string, object?>
        {
            ["windowTitleHash"] = "Patient Record"
        };
        var (isClean, violations) = ComplianceBoundary.ValidateFields(fields);
        Assert.False(isClean);
    }

    [Fact]
    public void Validate_VersionNumber_NotFlagged()
    {
        var json = """{"version":"3.4.0"}""";
        var (isClean, _) = ComplianceBoundary.Validate(json);
        Assert.True(isClean);
    }

    [Fact]
    public void Validate_WithPhoneNumber_Rejects()
    {
        var json = """{"data":"Call 555-123-4567"}""";
        var (isClean, _) = ComplianceBoundary.Validate(json);
        Assert.False(isClean);
    }

    [Fact]
    public void Validate_WithNamePair_Rejects()
    {
        var json = """{"patient":"John Smith"}""";
        var (isClean, _) = ComplianceBoundary.Validate(json);
        Assert.False(isClean);
    }

    [Fact]
    public void Validate_IpAddress_NotTreatedAsVersion()
    {
        var json = """{"server":"192.168.0.10"}""";
        var (isClean, _) = ComplianceBoundary.Validate(json);
        // IP addresses should be flagged (HIPAA identifier #15)
        Assert.False(isClean);
    }
}
