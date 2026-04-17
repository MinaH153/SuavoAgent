using System.Text;
using SuavoAgent.Contracts.Models;
using SuavoAgent.Core.Receipts;
using Xunit;

namespace SuavoAgent.Core.Tests.Receipts;

public class DeliveryReceiptGeneratorTests
{
    private static readonly byte[] FileMagic = "SVRC"u8.ToArray();
    private const byte FileVersion = 1;
    private const int HeaderLength = 5; // magic (4) + version (1)

    private static DeliveryWritebackCommand SampleCommand() => new(
        TaskId: "task-12345678-abcd",
        RxNumber: "98765",
        FillNumber: 1,
        ExternalSaleId: "SALE-001",
        RecipientFirstName: "Jane",
        RecipientLastName: "D",
        RecipientIdType: 1,
        RecipientIdValue: "D1234567",
        RecipientIdState: "CA",
        SignatureSvg: "<svg xmlns=\"http://www.w3.org/2000/svg\"><path d='M10 80 Q 95 10 180 80'/></svg>",
        Price: 12.50m,
        Tax: 1.09m,
        CounselingStatus: 2,
        DeliveredAt: DateTimeOffset.UtcNow);

    private static byte[] WrapForRead(byte[] payload)
    {
        var framed = new byte[HeaderLength + payload.Length];
        Buffer.BlockCopy(FileMagic, 0, framed, 0, FileMagic.Length);
        framed[FileMagic.Length] = FileVersion;
        Buffer.BlockCopy(payload, 0, framed, HeaderLength, payload.Length);
        return framed;
    }

    [Fact]
    public void GenerateHtml_ContainsRxNumber()
    {
        var gen = new DeliveryReceiptGenerator();
        var html = gen.GenerateHtml(SampleCommand(), "Test Pharmacy");
        Assert.Contains("98765", html);
        Assert.Contains("Test Pharmacy", html);
        Assert.Contains("DELIVERED", html);
    }

    [Fact]
    public void GenerateHtml_IncludesSanitizedSignature()
    {
        var gen = new DeliveryReceiptGenerator();
        var html = gen.GenerateHtml(SampleCommand(), "Test Pharmacy");
        Assert.Contains("<svg", html);
        Assert.Contains("Recipient Signature", html);
    }

    [Fact]
    public void GenerateHtml_MasksRecipientId()
    {
        var gen = new DeliveryReceiptGenerator();
        var html = gen.GenerateHtml(SampleCommand(), "Test Pharmacy");
        Assert.Contains("****4567", html);
        Assert.DoesNotContain("D1234567", html);
    }

    [Fact]
    public void GenerateHtml_CustomBranding()
    {
        var branding = new ReceiptBranding
        {
            CompanyName = "Custom Fleet",
            PrimaryColor = "#ff0000",
            FooterText = "Custom Footer"
        };
        var gen = new DeliveryReceiptGenerator(branding);
        var html = gen.GenerateHtml(SampleCommand(), "Test Pharmacy");
        Assert.Contains("Custom Fleet", html);
        Assert.Contains("#ff0000", html);
        Assert.Contains("Custom Footer", html);
    }

    [Fact]
    public void GenerateHtml_NoSignature_ShowsPlaceholder()
    {
        var cmd = SampleCommand() with { SignatureSvg = null };
        var gen = new DeliveryReceiptGenerator();
        var html = gen.GenerateHtml(cmd, "Test Pharmacy");
        Assert.Contains("No signature captured", html);
    }

    [Fact]
    public void GenerateHtml_ValidBase64Proof_IsEmbedded()
    {
        // Construct a minimal valid JPEG: FF D8 FF + padding to keep base64 clean
        var jpegPrefix = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 };
        var b64 = Convert.ToBase64String(jpegPrefix);
        var gen = new DeliveryReceiptGenerator();
        var html = gen.GenerateHtml(SampleCommand(), "Test Pharmacy", proofImageBase64: b64);
        Assert.Contains($"data:image/jpeg;base64,{b64}", html);
        Assert.Contains("Proof of Delivery", html);
    }

    [Fact]
    public void GenerateHtml_MaliciousProofPayload_IsRejected()
    {
        var gen = new DeliveryReceiptGenerator();
        var html = gen.GenerateHtml(SampleCommand(), "Test Pharmacy",
            proofImageBase64: "\"><script>alert(1)</script>");
        Assert.DoesNotContain("<script>alert(1)</script>", html);
        Assert.DoesNotContain("Proof of Delivery", html);
    }

    [Fact]
    public void GenerateHtml_ScriptInjectionInRecipientName_IsEncoded()
    {
        var cmd = SampleCommand() with
        {
            RecipientFirstName = "<script>alert(1)</script>",
            RecipientLastName = "\"><img src=x onerror=alert(2)>"
        };
        var gen = new DeliveryReceiptGenerator();
        var html = gen.GenerateHtml(cmd, "Test Pharmacy");
        Assert.DoesNotContain("<script>alert(1)</script>", html);
        Assert.DoesNotContain("<img src=x onerror=", html);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
        Assert.Contains("&lt;img src=x onerror=alert(2)&gt;", html);
    }

    [Fact]
    public void GenerateHtml_ScriptInjectionInPharmacyName_IsEncoded()
    {
        var gen = new DeliveryReceiptGenerator();
        var html = gen.GenerateHtml(SampleCommand(), "<script>x()</script>");
        Assert.DoesNotContain("<script>x()</script>", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    [Fact]
    public void GenerateHtml_MaliciousBrandingColor_FallsBackToDefault()
    {
        var branding = new ReceiptBranding
        {
            PrimaryColor = "red;}</style><script>alert(1)</script>"
        };
        var gen = new DeliveryReceiptGenerator(branding);
        var html = gen.GenerateHtml(SampleCommand(), "Test Pharmacy");
        Assert.DoesNotContain("<script>alert(1)</script>", html);
        Assert.Contains(ReceiptBranding.Default.PrimaryColor, html);
    }

    [Fact]
    public void GenerateHtml_SvgScriptTag_IsStripped()
    {
        var cmd = SampleCommand() with { SignatureSvg = "<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script><path d='M1 1'/></svg>" };
        var gen = new DeliveryReceiptGenerator();
        var html = gen.GenerateHtml(cmd, "Test Pharmacy");
        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("alert(1)", html);
        Assert.Contains("<path", html);
    }

    [Fact]
    public void GenerateHtml_SvgStyleImportJavascript_IsStripped()
    {
        var cmd = SampleCommand() with
        {
            SignatureSvg = "<svg xmlns=\"http://www.w3.org/2000/svg\"><style>@import url(javascript:alert(1));</style><path d='M1 1'/></svg>"
        };
        var gen = new DeliveryReceiptGenerator();
        var html = gen.GenerateHtml(cmd, "Test Pharmacy");
        Assert.DoesNotContain("@import url(javascript", html);
        Assert.DoesNotContain("javascript:alert", html);
    }

    [Fact]
    public void GenerateHtml_SvgUseWithXlinkHref_IsStripped()
    {
        var cmd = SampleCommand() with
        {
            SignatureSvg = "<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\"><use xlink:href=\"javascript:alert(1)\"/></svg>"
        };
        var gen = new DeliveryReceiptGenerator();
        var html = gen.GenerateHtml(cmd, "Test Pharmacy");
        Assert.DoesNotContain("javascript:", html);
        Assert.DoesNotContain("<use", html);
    }

    [Fact]
    public void GenerateHtml_SvgOnloadHandler_IsStripped()
    {
        var cmd = SampleCommand() with
        {
            SignatureSvg = "<svg xmlns=\"http://www.w3.org/2000/svg\" onload=\"alert(1)\"><path d='M1 1'/></svg>"
        };
        var gen = new DeliveryReceiptGenerator();
        var html = gen.GenerateHtml(cmd, "Test Pharmacy");
        Assert.DoesNotContain("onload", html);
        Assert.DoesNotContain("alert(1)", html);
    }

    [Fact]
    public void GenerateHtml_SvgAnimateWithOnbegin_IsStripped()
    {
        var cmd = SampleCommand() with
        {
            SignatureSvg = "<svg xmlns=\"http://www.w3.org/2000/svg\"><animate onbegin=\"alert(1)\" attributeName=\"x\"/></svg>"
        };
        var gen = new DeliveryReceiptGenerator();
        var html = gen.GenerateHtml(cmd, "Test Pharmacy");
        Assert.DoesNotContain("onbegin", html);
        Assert.DoesNotContain("<animate", html);
    }

    [Fact]
    public void GenerateHtml_MalformedSvg_YieldsEmpty()
    {
        var cmd = SampleCommand() with { SignatureSvg = "<svg><unclosed" };
        var gen = new DeliveryReceiptGenerator();
        var html = gen.GenerateHtml(cmd, "Test Pharmacy");
        Assert.Contains("Recipient Signature", html);
        Assert.DoesNotContain("<unclosed", html);
    }

    [Fact]
    public void GenerateHtml_SvgWithDtdDoctype_IsStripped()
    {
        var cmd = SampleCommand() with
        {
            SignatureSvg = "<!DOCTYPE svg [<!ENTITY xxe SYSTEM \"file:///etc/passwd\">]><svg xmlns=\"http://www.w3.org/2000/svg\"><text>&xxe;</text></svg>"
        };
        var gen = new DeliveryReceiptGenerator();
        var html = gen.GenerateHtml(cmd, "Test Pharmacy");
        Assert.DoesNotContain("passwd", html);
        Assert.DoesNotContain("ENTITY", html);
    }

    [Fact]
    public void ReadReceipt_WithValidMagicHeader_DecryptsCorrectly()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "SuavoAgent-test-receipts-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            var cmd = SampleCommand();
            var gen = new DeliveryReceiptGenerator();
            var html = gen.GenerateHtml(cmd, "Test Pharmacy");

            var plain = Encoding.UTF8.GetBytes(html);
            var cipher = OperatingSystem.IsWindows()
                ? System.Security.Cryptography.ProtectedData.Protect(plain, null,
                    System.Security.Cryptography.DataProtectionScope.LocalMachine)
                : plain;

            var filePath = Path.Combine(tempDir, "receipt-test.dat");
            File.WriteAllBytes(filePath, WrapForRead(cipher));

            var decrypted = DeliveryReceiptGenerator.ReadReceipt(filePath);
            Assert.NotNull(decrypted);
            Assert.Contains("98765", decrypted);
            Assert.Contains("<!DOCTYPE", decrypted);
        }
        finally { Directory.Delete(tempDir, recursive: true); }
    }

    [Fact]
    public void ReadReceipt_WithBadMagic_ReturnsNull()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "SuavoAgent-test-receipts-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            var filePath = Path.Combine(tempDir, "receipt-bad.dat");
            File.WriteAllBytes(filePath, "BOGUS"u8.ToArray());
            Assert.Null(DeliveryReceiptGenerator.ReadReceipt(filePath));
        }
        finally { Directory.Delete(tempDir, recursive: true); }
    }

    [Fact]
    public void ReadReceipt_Truncated_ReturnsNull()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "SuavoAgent-test-receipts-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            var filePath = Path.Combine(tempDir, "receipt-short.dat");
            File.WriteAllBytes(filePath, new byte[] { 0x53, 0x56 }); // only 2 bytes
            Assert.Null(DeliveryReceiptGenerator.ReadReceipt(filePath));
        }
        finally { Directory.Delete(tempDir, recursive: true); }
    }

    [Fact]
    public void ReadReceipt_MissingFile_ReturnsNull()
    {
        Assert.Null(DeliveryReceiptGenerator.ReadReceipt(Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid().ToString("N") + ".dat")));
    }

    [Fact]
    public void GenerateHtml_CalculatesTotal()
    {
        var gen = new DeliveryReceiptGenerator();
        var html = gen.GenerateHtml(SampleCommand(), "Test Pharmacy");
        Assert.Contains("$13.59", html);
    }

    [Fact]
    public void GenerateHtml_ContainsContentSecurityPolicy()
    {
        var gen = new DeliveryReceiptGenerator();
        var html = gen.GenerateHtml(SampleCommand(), "Test Pharmacy");
        Assert.Contains("Content-Security-Policy", html);
        Assert.Contains("default-src 'none'", html);
    }

    [Fact]
    public void GenerateHtml_SvgFontFamilyCssEscape_IsStripped()
    {
        // font-family is removed from AllowedAttributes — CSS escape injection blocked
        var cmd = SampleCommand() with
        {
            SignatureSvg = "<svg xmlns=\"http://www.w3.org/2000/svg\"><text font-family=\"serif&#x5c;0022;expression(alert(1))\" x=\"10\" y=\"20\">X</text></svg>"
        };
        var gen = new DeliveryReceiptGenerator();
        var html = gen.GenerateHtml(cmd, "Test Pharmacy");
        Assert.DoesNotContain("font-family", html.Replace("<style>", "").Split("</style>")[^1]); // not in SVG output
        Assert.DoesNotContain("expression(", html);
    }

    [Fact]
    public void GenerateHtml_SvgFontFamilyAttribute_IsDropped()
    {
        var cmd = SampleCommand() with
        {
            SignatureSvg = "<svg xmlns=\"http://www.w3.org/2000/svg\"><text font-family=\"Arial\" x=\"0\" y=\"10\">sig</text></svg>"
        };
        var gen = new DeliveryReceiptGenerator();
        var html = gen.GenerateHtml(cmd, "Test Pharmacy");
        // font-family should not appear in the sanitized SVG portion (it may appear in the <style> block)
        var svgStart = html.IndexOf("<svg", StringComparison.Ordinal);
        var svgEnd = html.IndexOf("</svg>", svgStart, StringComparison.Ordinal) + 6;
        var svgPart = html[svgStart..svgEnd];
        Assert.DoesNotContain("font-family", svgPart);
    }
}
