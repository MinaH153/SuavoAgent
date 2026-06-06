using Serilog;
using SuavoAgent.Helper.Security;
using Xunit;

namespace SuavoAgent.Helper.Tests.Security;

public sealed class FileAccessAttributorFactoryTests
{
    private static readonly ILogger Log = new LoggerConfiguration().CreateLogger();

    [Fact]
    public void Null_ReturnsNullAttributor_TheSafeDefault()
    {
        var a = FileAccessAttributorFactory.Create(HoneytokenAttributorMode.Null, "nonce", Log);
        Assert.IsType<NullFileAccessAttributor>(a);
    }

    [Fact]
    public void EtwBroker_ReturnsEtwBrokerAttributor()
    {
        var a = FileAccessAttributorFactory.Create(HoneytokenAttributorMode.EtwBroker, "nonce", Log);
        Assert.IsType<EtwBrokerFileAccessAttributor>(a);
    }

    [Fact]
    public void UnknownMode_FallsBackToNull()
    {
        // An out-of-range enum value must never silently construct a non-Null attributor.
        var a = FileAccessAttributorFactory.Create((HoneytokenAttributorMode)999, "nonce", Log);
        Assert.IsType<NullFileAccessAttributor>(a);
    }
}
