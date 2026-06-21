// tests/SuavoAgent.Setup.Tests/Doctor/DoctorModeRoutingTests.cs
using Xunit;

namespace SuavoAgent.Setup.Tests.Doctor;

public class DoctorModeRoutingTests
{
    [Fact]
    public void Detects_doctor_flag_case_insensitive()
    {
        Assert.True(SuavoAgent.Setup.Program.IsDoctorMode(new[] { "--Doctor" }));
        Assert.False(SuavoAgent.Setup.Program.IsDoctorMode(new[] { "--console" }));
    }
}
