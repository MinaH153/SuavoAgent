using SuavoAgent.Setup.InstallerSupport;
using Xunit;

namespace SuavoAgent.Setup.Tests.InstallerSupport;

public sealed class MsiInstallerInvocationTests : IDisposable
{
    private const string ProductCode =
        "{A1111111-B222-C333-D444-E55555555555}";
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "suavo-msi-invocation-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void Exact_hidden_contract_derives_stable_unique_lowerhex_identity()
    {
        var firstData = Data("restart-manager-session-a");
        var secondData = Data("restart-manager-session-b");

        Assert.True(MsiInstallerInvocation.TryParse(firstData, out var first));
        Assert.True(MsiInstallerInvocation.TryParse(firstData, out var repeated));
        Assert.True(MsiInstallerInvocation.TryParse(secondData, out var second));

        Assert.Equal(first.InvocationId, repeated.InvocationId);
        Assert.NotEqual(first.InvocationId, second.InvocationId);
        Assert.True(MsiInstallerInvocation.IsValidInvocationId(first.InvocationId));
        Assert.Equal(ProductCode, first.ProductCode);
        Assert.Equal(@"C:\rehearsal\SuavoAgent.msi", first.OriginalDatabase);
        Assert.Equal(@"C:\Program Files\Suavo\Agent\", first.InstallDirectory);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("v1|bad|session|C:\\rehearsal\\SuavoAgent.msi|C:\\Program Files\\Suavo\\Agent\\")]
    [InlineData("v1|{A1111111-B222-C333-D444-E55555555555}||C:\\rehearsal\\SuavoAgent.msi|C:\\Program Files\\Suavo\\Agent\\")]
    [InlineData("v1|{A1111111-B222-C333-D444-E55555555555}|session|path")]
    [InlineData("v2|{A1111111-B222-C333-D444-E55555555555}|session|C:\\rehearsal\\SuavoAgent.msi|C:\\Program Files\\Suavo\\Agent\\")]
    public void Parser_rejects_every_non_exact_contract(string? value) =>
        Assert.False(MsiInstallerInvocation.TryParse(value, out _));

    [Fact]
    public void Arm_refuses_existing_token_without_changing_it()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "active.json");
        var activation = new FileMsiInstallerTransactionActivation(
            path,
            static _ => { });
        var first = ParseId(Data("restart-manager-session-a"));
        var second = ParseId(Data("restart-manager-session-b"));

        activation.Arm(first);
        activation.RequireCurrent(first);
        var priorBytes = File.ReadAllBytes(path);

        Assert.Throws<IOException>(() => activation.Arm(second));

        Assert.Equal(priorBytes, File.ReadAllBytes(path));
        Assert.True(File.Exists(path));
        activation.RequireCurrent(first);
        Assert.Throws<InvalidDataException>(() => activation.Disarm(second));
        activation.Disarm(first);
        Assert.False(File.Exists(path));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (DirectoryNotFoundException) { }
    }

    private static string Data(string sessionKey) =>
        MsiInstallerInvocation.BuildForTests(
            ProductCode,
            sessionKey,
            @"C:\rehearsal\SuavoAgent.msi");

    private static string ParseId(string data)
    {
        Assert.True(MsiInstallerInvocation.TryParse(data, out var invocation));
        return invocation.InvocationId;
    }
}
