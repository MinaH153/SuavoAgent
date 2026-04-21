using SuavoAgent.Watchdog;
using Xunit;

namespace SuavoAgent.Watchdog.Tests;

public class ServiceCommandTests
{
    [Theory]
    [InlineData("STATE              : 4  RUNNING", ServiceState.Running)]
    [InlineData("STATE              : 1  STOPPED", ServiceState.Stopped)]
    [InlineData("STATE              : 2  START_PENDING", ServiceState.StartPending)]
    [InlineData("STATE              : 3  STOP_PENDING", ServiceState.StopPending)]
    public void ParseState_KnownOutputs(string snippet, ServiceState expected)
    {
        Assert.Equal(expected, ServiceCommand.ParseState(snippet));
    }

    [Fact]
    public void ParseState_EmptyOutput_ReturnsUnknown()
    {
        Assert.Equal(ServiceState.Unknown, ServiceCommand.ParseState(""));
    }

    [Fact]
    public void ParseState_NoStateLine_ReturnsUnknown()
    {
        Assert.Equal(ServiceState.Unknown, ServiceCommand.ParseState("some noise without the magic keyword"));
    }

    [Fact]
    public void ParseState_FullQueryExOutput_Running()
    {
        // Realistic sc.exe queryex output (trimmed)
        var output = @"
SERVICE_NAME: SuavoAgent.Core
        TYPE               : 10  WIN32_OWN_PROCESS
        STATE              : 4  RUNNING
                                (STOPPABLE, NOT_PAUSABLE, ACCEPTS_SHUTDOWN)
        WIN32_EXIT_CODE    : 0  (0x0)
        SERVICE_EXIT_CODE  : 0  (0x0)
        CHECKPOINT         : 0x0
        WAIT_HINT          : 0x0
        PID                : 4272
        FLAGS              :
";
        Assert.Equal(ServiceState.Running, ServiceCommand.ParseState(output));
    }
}
