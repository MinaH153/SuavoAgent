using System.Reflection;
using SuavoAgent.Setup;
using Xunit;

namespace SuavoAgent.Setup.Tests;

public sealed class SqlCredentialDiscoveryParsingTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "suavo-sql-discovery-parsing-" + Guid.NewGuid().ToString("N"));

    public SqlCredentialDiscoveryParsingTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData("<newTechDataConfiguration host=\"rx-host\" />", "rx-host")]
    [InlineData("<newTechDataConfiguration server=\"rx-server\" />", "rx-server")]
    [InlineData("<newTechDataConfiguration><host>rx-child</host></newTechDataConfiguration>", "rx-child")]
    [InlineData("<newTechDataConfiguration><server>rx-child-server</server></newTechDataConfiguration>", "rx-child-server")]
    [InlineData("<configuration><other host=\"regex-host\" /></configuration>", "regex-host")]
    [InlineData("<configuration><other server=\"regex-server\" /></configuration>", "regex-server")]
    public void ConfigParser_FindsSupportedHostShapes(string xml, string expected)
    {
        var path = Write("PioneerPharmacy.exe.config", xml);

        Assert.Equal(expected, InvokeExtractHost(path));
    }

    [Fact]
    public void ConfigParser_RejectsDtdAndExternalEntityPayloads()
    {
        var path = Write(
            "xxe.config",
            "<!DOCTYPE x [<!ENTITY ext SYSTEM \"file:///etc/passwd\">]>" +
            "<newTechDataConfiguration host=\"&ext;\" />");

        Assert.Null(InvokeExtractHost(path));
    }

    [Theory]
    [InlineData("not xml")]
    [InlineData("<configuration>")]
    [InlineData("<configuration />")]
    public void ConfigParser_ReturnsNullForMalformedOrHostlessInput(string text)
    {
        Assert.Null(InvokeExtractHost(Write(Guid.NewGuid() + ".config", text)));
    }

    [Fact]
    public void ConfigParser_ReturnsNullForMissingFile()
    {
        Assert.Null(InvokeExtractHost(Path.Combine(_root, "missing.config")));
    }

    [Theory]
    [InlineData("Data Source=sql01;Initial Catalog=Rx;Integrated Security=true", "sql01", "Rx")]
    [InlineData("Server=sql02;Database=Rx2;Integrated Security=SSPI", "sql02", "Rx2")]
    [InlineData("Address=10.0.0.5,1433;Trusted_Connection=yes", "10.0.0.5,1433", "PioneerPharmacySystem")]
    [InlineData("Addr=sql04;Trusted_Connection=YES", "sql04", "PioneerPharmacySystem")]
    public void ConnectionStringParser_RecognizesWindowsAuthAliases(
        string connectionString,
        string expectedServer,
        string expectedDatabase)
    {
        var credentials = InvokeParse(connectionString);

        Assert.NotNull(credentials);
        Assert.Equal(expectedServer, credentials.Server);
        Assert.Equal(expectedDatabase, credentials.Database);
        Assert.True(credentials.IsWindowsAuth);
        Assert.Null(credentials.User);
        Assert.Null(credentials.Password);
    }

    [Theory]
    [InlineData("Server=sql01;Database=Rx;User ID=operator;Password=secret", "operator", "secret")]
    [InlineData("Data Source=sql02;UID=user2;PWD=pw2", "user2", "pw2")]
    [InlineData("Address=sql03;User=user3;Password=pw3", "user3", "pw3")]
    public void ConnectionStringParser_RecognizesSqlAuthAliases(
        string connectionString,
        string expectedUser,
        string expectedPassword)
    {
        var credentials = InvokeParse(connectionString);

        Assert.NotNull(credentials);
        Assert.False(credentials.IsWindowsAuth);
        Assert.Equal(expectedUser, credentials.User);
        Assert.Equal(expectedPassword, credentials.Password);
    }

    [Fact]
    public void ConnectionStringParser_IgnoresMalformedSegmentsAndUsesLastExactKey()
    {
        var credentials = InvokeParse(
            "garbage;=bad;Server=old;SERVER=new;Database=Rx;Integrated Security=true;");

        Assert.NotNull(credentials);
        Assert.Equal("new", credentials.Server);
        Assert.Equal("Rx", credentials.Database);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Database=Rx")]
    [InlineData("User ID=operator;Password=secret")]
    [InlineData("Server=   ;Integrated Security=true")]
    [InlineData("Server=sql01;Integrated Security=false")]
    [InlineData("Server=sql01;Integrated Security=false;User ID=operator")]
    [InlineData("Server=sql01;Integrated Security=false;Password=secret")]
    [InlineData("Server=sql01;User ID=   ;Password=secret")]
    [InlineData("Server=sql01;User ID=operator;Password=   ")]
    public void ConnectionStringParser_RejectsMissingServerOrIncompleteSqlAuth(
        string connectionString)
    {
        Assert.Null(InvokeParse(connectionString));
    }

    private string Write(string name, string contents)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, contents);
        return path;
    }

    private static string? InvokeExtractHost(string path) =>
        (string?)Method("ExtractHostFromConfig").Invoke(null, [path]);

    private static SqlCredentialDiscovery.SqlCredentials? InvokeParse(string value) =>
        (SqlCredentialDiscovery.SqlCredentials?)Method("ParseConnectionString")
            .Invoke(null, [value]);

    private static MethodInfo Method(string name)
    {
        var method = typeof(SqlCredentialDiscovery).GetMethod(
            name,
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return method;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
