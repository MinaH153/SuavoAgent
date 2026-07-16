using System.Reflection;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.State;

public sealed class ProductionAclBoundaryPathTests
{
    [Theory]
    [InlineData("")]
    [InlineData("nested/state.db")]
    [InlineData("../state.db")]
    public void InvalidProtectedFilenameIsRejectedBeforePlatformAclInspection(
        string expectedFileName)
    {
        var error = InvokeValidatePath("/tmp/outside-state.db", expectedFileName, false);

        Assert.IsType<ArgumentException>(error);
        Assert.Equal("expectedFileName", ((ArgumentException)error).ParamName);
    }

    [Fact]
    public void PathOutsideFixedProgramDataBoundaryIsRejectedBeforeAclInspection()
    {
        var error = InvokeValidatePath(
            Path.Combine(Path.GetTempPath(), "outside-state.db"),
            "state.db",
            false);

        Assert.IsType<UnauthorizedAccessException>(error);
        Assert.Contains("fixed ProgramData boundary", error.Message);
    }

    private static Exception InvokeValidatePath(
        string path,
        string expectedFileName,
        bool mustExist)
    {
        var method = typeof(ProductionAclBoundary).GetMethod(
            "ValidatePath", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ValidatePath missing.");
        var wrapper = Assert.Throws<TargetInvocationException>(
            () => method.Invoke(null, [path, expectedFileName, mustExist]));
        return Assert.IsAssignableFrom<Exception>(wrapper.InnerException);
    }
}
