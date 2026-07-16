using System.Reflection;
using System.Text;
using System.Text.Json;
using SuavoAgent.Core.Config;
using Xunit;

namespace SuavoAgent.Core.Tests.Config;

public sealed class PioneerRxSqlCertificatePinVerifierBoundaryTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "suavo-sql-pin-boundary-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ProductionResolver_RejectsBypassAndNonWindowsPinBeforeTrustingAPath()
    {
        Assert.Throws<ArgumentNullException>(() =>
            PioneerRxSqlCertificatePinVerifier.ResolveProduction(null!));

        Assert.Throws<InvalidOperationException>(() =>
            PioneerRxSqlCertificatePinVerifier.ResolveProduction(new AgentOptions
            {
                SqlTrustServerCertificate = true,
            }));

        var noPin = new AgentOptions
        {
            ValidatedSqlServerCertificatePath = "stale-path",
        };
        Assert.Null(PioneerRxSqlCertificatePinVerifier.ResolveProduction(noPin));
        Assert.Null(noPin.ValidatedSqlServerCertificatePath);

        if (!OperatingSystem.IsWindows())
        {
            Assert.Throws<PlatformNotSupportedException>(() =>
                PioneerRxSqlCertificatePinVerifier.ResolveProduction(new AgentOptions
                {
                    SqlServerCertificateSha256 = new string('a', 64),
                }));
        }
    }

    [Theory]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", true)]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", true)]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", false)]
    [InlineData("gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg", false)]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void DigestShape_IsExactlySixtyFourLowerHex(string? value, bool accepted)
    {
        Assert.Equal(accepted, InvokeBool("IsLowerHex64", value));
    }

    [Theory]
    [InlineData("sql_certificate_ok", "fallback", "sql_certificate_ok")]
    [InlineData("contains-dash", "fallback", "fallback")]
    [InlineData("UPPER", "fallback", "fallback")]
    [InlineData("", "fallback", "fallback")]
    [InlineData(null, "fallback", "fallback")]
    public void StableCode_AllowsOnlyBoundedLowerMachineCodes(
        string? value,
        string fallback,
        string expected)
    {
        Assert.Equal(expected, InvokeString("StableCode", value, fallback));
    }

    [Fact]
    public void StableCode_RejectsOverEightyCharacters()
    {
        Assert.Equal("fallback", InvokeString("StableCode", new string('a', 81), "fallback"));
    }

    [Theory]
    [InlineData("{\"a\":1,\"b\":2}", true)]
    [InlineData("{\"a\":1,\"a\":2}", false)]
    [InlineData("{\"A\":1,\"a\":2}", false)]
    [InlineData("{\"a\":{\"x\":1,\"x\":2},\"b\":2}", true)]
    [InlineData("[]", true)]
    public void StrictJson_RootPropertiesMustBeCaseInsensitiveUnique(string json, bool accepted)
    {
        Assert.Equal(
            accepted,
            InvokeBoolSpan("HasUniqueRootProperties", Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void StrictJson_MalformedDocumentCannotBeMistakenForUnique()
    {
        Assert.ThrowsAny<JsonException>(() =>
            InvokeBoolSpan("HasUniqueRootProperties", Encoding.UTF8.GetBytes("{")));
    }

    [Fact]
    public void StrictReader_RejectsMissingDirectoryEmptyOversizeMalformedAndDuplicateFiles()
    {
        Directory.CreateDirectory(_directory);
        AssertNullRead(Path.Combine(_directory, "missing.json"));
        AssertNullRead(_directory);

        var empty = Path.Combine(_directory, "empty.json");
        File.WriteAllBytes(empty, Array.Empty<byte>());
        AssertNullRead(empty);

        var oversize = Path.Combine(_directory, "oversize.json");
        File.WriteAllBytes(oversize, new byte[(64 * 1024) + 1]);
        AssertNullRead(oversize);

        var malformed = Path.Combine(_directory, "malformed.json");
        File.WriteAllText(malformed, "{");
        AssertNullRead(malformed);

        var duplicate = Path.Combine(_directory, "duplicate.json");
        File.WriteAllText(duplicate, "{\"a\":1,\"A\":2}");
        AssertNullRead(duplicate);
    }

    [Fact]
    public void StrictReader_DeserializesOnlyExactWellFormedObject()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "valid.json");
        File.WriteAllText(path, "{\"Value\":17}");

        var result = InvokeReadStrict<StrictFixture>(path);

        Assert.NotNull(result);
        Assert.Equal(17, result!.Value);
    }

    private static void AssertNullRead(string path) =>
        Assert.Null(InvokeReadStrict<StrictFixture>(path));

    private static T? InvokeReadStrict<T>(string path)
    {
        var method = typeof(PioneerRxSqlCertificatePinVerifier).GetMethod(
            "ReadStrict",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (T?)method!.MakeGenericMethod(typeof(T)).Invoke(null, [path]);
    }

    private static bool InvokeBool(string methodName, object? value)
    {
        var method = typeof(PioneerRxSqlCertificatePinVerifier).GetMethod(
            methodName,
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return Assert.IsType<bool>(method!.Invoke(null, [value]));
    }

    private static bool InvokeBoolSpan(string methodName, byte[] value)
    {
        var method = typeof(PioneerRxSqlCertificatePinVerifier).GetMethod(
            methodName,
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        // ReadOnlySpan cannot be boxed, so call the method through its byte[]
        // compatible reflection binder is impossible. A tiny strongly typed
        // delegate preserves the real implementation without copying logic.
        var callback = method!.CreateDelegate<UniqueRootDelegate>();
        return callback(value);
    }

    private static string InvokeString(string methodName, params object?[] values)
    {
        var method = typeof(PioneerRxSqlCertificatePinVerifier).GetMethod(
            methodName,
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return Assert.IsType<string>(method!.Invoke(null, values));
    }

    private delegate bool UniqueRootDelegate(ReadOnlySpan<byte> json);

    private sealed record StrictFixture(int Value);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }
}
