// tests/SuavoAgent.Setup.Tests/Doctor/DoctorReportTests.cs
using SuavoAgent.Setup.Doctor;
using SuavoAgent.Setup.Verify;
using Xunit;

namespace SuavoAgent.Setup.Tests.Doctor;

public class DoctorReportTests
{
    private static DoctorReport Make(params GateResult[] layers) => new("3.71.0", layers);

    [Fact]
    public void HasFailure_true_when_any_layer_fails()
        => Assert.True(Make(new GateResult("A", GateState.Ok, "x"), new GateResult("B", GateState.Fail, "y")).HasFailure);

    [Fact]
    public void HasFailure_false_when_only_warn_or_ok()
        => Assert.False(Make(new GateResult("A", GateState.Ok, "x"), new GateResult("B", GateState.Warn, "y")).HasFailure);

    [Fact]
    public void ToJson_includes_version_and_each_layer()
    {
        var json = DoctorReport.ToJson(Make(new GateResult("Brain", GateState.Fail, "native load failed")));
        Assert.Contains("3.71.0", json);
        Assert.Contains("Brain", json);
        Assert.Contains("Fail", json);
    }

    [Fact]
    public void ToTable_renders_each_layer_name_and_state()
    {
        var table = DoctorReport.ToTable(Make(new GateResult("SQL", GateState.Fail, "auth failing")));
        Assert.Contains("SQL", table);
        Assert.Contains("Fail", table);
        Assert.Contains("auth failing", table);
    }
}
