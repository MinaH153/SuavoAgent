using System.Text;
using System.Text.Json;
using Org.Webpki.Es6NumberSerialization;
using SuavoAgent.Diagnostics;
using Xunit;

namespace SuavoAgent.Diagnostics.Tests;

public sealed class Rfc8785CanonicalizerTests
{
    [Fact]
    public void OfficialCrossSystemValueVectorMatchesCanonicalBytes()
    {
        const string input =
            """
            {
              "numbers": [333333333.33333329, 1E30, 4.50, 2e-3, 0.000000000000000000000000001],
              "string": "\u20ac$\u000F\u000aA'\u0042\u0022\u005c\\\"\/",
              "literals": [null, true, false]
            }
            """;
        const string expected =
            """{"literals":[null,true,false],"numbers":[333333333.3333333,1e+30,4.5,0.002,1e-27],"string":"€$\u000f\nA'B\"\\\\\"/"}""";

        var canonical = Rfc8785Canonicalizer.Canonicalize(input);
        var bytes = Rfc8785Canonicalizer.CanonicalizeToUtf8(input);

        Assert.Equal(expected, canonical);
        Assert.Equal(Encoding.UTF8.GetBytes(expected), bytes);
    }

    [Fact]
    public void DuplicatePropertyNamesFailClosedWithoutEchoingTheName()
    {
        const string sensitiveName = "patient-name-sentinel";
        var exception = Assert.Throws<JsonException>(() =>
            Rfc8785Canonicalizer.Canonicalize(
                $$"""{"{{sensitiveName}}":1,"{{sensitiveName}}":2}"""));

        Assert.Equal("rfc8785_input_invalid", exception.Message);
        Assert.DoesNotContain(sensitiveName, exception.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[NaN]")]
    [InlineData("[Infinity]")]
    [InlineData("[-Infinity]")]
    [InlineData("[1e400]")]
    [InlineData("{\"x\":01}")]
    [InlineData("{\"x\":1,}")]
    [InlineData("{\"x\":1}//comment")]
    [InlineData("{\"x\":1}{\"y\":2}")]
    [InlineData("true")]
    public void NonFiniteOrOverflowingJsonNumbersFailClosed(string input)
    {
        var exception = Assert.Throws<JsonException>(() =>
            Rfc8785Canonicalizer.Canonicalize(input));
        Assert.Equal("rfc8785_input_invalid", exception.Message);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void NumberSerializerRejectsNonFiniteValues(double value) =>
        Assert.Throws<ArgumentException>(() => NumberToJson.SerializeNumber(value));

    [Fact]
    public void LoneSurrogateCannotBecomeSignableReplacementBytes()
    {
        var input = "{\"x\":\"\ud800\"}";
        var exception = Assert.Throws<JsonException>(() =>
            Rfc8785Canonicalizer.CanonicalizeToUtf8(input));
        Assert.Equal("rfc8785_input_invalid", exception.Message);
    }

    [Fact]
    public void EscapedLoneSurrogateFailsClosed()
    {
        var exception = Assert.Throws<JsonException>(() =>
            Rfc8785Canonicalizer.CanonicalizeToUtf8("""{"x":"\ud800"}"""));
        Assert.Equal("rfc8785_input_invalid", exception.Message);
    }

    [Fact]
    public void DuplicateDecodedPropertyNamesFailClosed()
    {
        var exception = Assert.Throws<JsonException>(() =>
            Rfc8785Canonicalizer.Canonicalize("""{"a":1,"\u0061":2}"""));
        Assert.Equal("rfc8785_input_invalid", exception.Message);
    }

    [Theory]
    [InlineData("0000000000000001", "5e-324")]
    [InlineData("8000000000000000", "0")]
    [InlineData("0010000000000000", "2.2250738585072014e-308")]
    [InlineData("7fefffffffffffff", "1.7976931348623157e+308")]
    [InlineData("444b1ae4d6e2ef50", "1e+21")]
    [InlineData("3eb0c6f7a0b5ed8d", "0.000001")]
    [InlineData("3eb0c6f7a0b5ed8c", "9.999999999999997e-7")]
    [InlineData("4340000000000001", "9007199254740994")]
    public void OfficialEs6Ieee754EdgeVectorsMatch(string ieeeHex, string expected)
    {
        var bits = unchecked((long)Convert.ToUInt64(ieeeHex, 16));
        var value = BitConverter.Int64BitsToDouble(bits);
        Assert.Equal(expected, NumberToJson.SerializeNumber(value));
    }
}
