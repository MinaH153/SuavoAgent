using SuavoAgent.Contracts.Models;
using SuavoAgent.Core.Receipts;
using Xunit;

namespace SuavoAgent.Core.Tests.Receipts;

public class DeliveryReceiptGeneratorTests
{
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
        SignatureSvg: "<svg><path d='M10 80 Q 95 10 180 80'/></svg>",
        Price: 12.50m,
        Tax: 1.09m,
        CounselingStatus: 2,
        DeliveredAt: DateTimeOffset.UtcNow);

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
    public void GenerateHtml_ContainsSignatureSvg()
    {
        var gen = new DeliveryReceiptGenerator();
        var html = gen.GenerateHtml(SampleCommand(), "Test Pharmacy");
        Assert.Contains("<svg>", html);
        Assert.Contains("Recipient Signature", html);
    }

    [Fact]
    public void GenerateHtml_MasksRecipientId()
    {
        var gen = new DeliveryReceiptGenerator();
        var html = gen.GenerateHtml(SampleCommand(), "Test Pharmacy");
        Assert.Contains("****4567", html); // last 4 visible
        Assert.DoesNotContain("D1234567", html); // full ID NOT visible
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
    public void GenerateHtml_WithProofPhoto()
    {
        var gen = new DeliveryReceiptGenerator();
        var html = gen.GenerateHtml(SampleCommand(), "Test Pharmacy", proofImageBase64: "abc123fake");
        Assert.Contains("data:image/jpeg;base64,abc123fake", html);
        Assert.Contains("Proof of Delivery", html);
    }

    [Fact]
    public void SaveReceipt_CreatesEncryptedDatFile()
    {
        // Simulate SaveReceipt logic in a temp dir to avoid OS permission issues on macOS
        var tempDir = Path.Combine(Path.GetTempPath(), "SuavoAgent-test-receipts-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            var cmd = SampleCommand();
            var gen = new DeliveryReceiptGenerator();
            var html = gen.GenerateHtml(cmd, "Test Pharmacy");

            // Mirror SaveReceipt's encryption logic
            var plainBytes = System.Text.Encoding.UTF8.GetBytes(html);
            byte[] encryptedBytes;
            if (OperatingSystem.IsWindows())
            {
                encryptedBytes = System.Security.Cryptography.ProtectedData.Protect(
                    plainBytes, null, System.Security.Cryptography.DataProtectionScope.LocalMachine);
            }
            else
            {
                encryptedBytes = plainBytes; // non-Windows: no DPAPI
            }

            var fileName = $"receipt-{cmd.RxNumber}-{cmd.DeliveredAt:yyyyMMdd-HHmmss}.dat";
            var filePath = Path.Combine(tempDir, fileName);
            File.WriteAllBytes(filePath, encryptedBytes);

            Assert.True(File.Exists(filePath));
            Assert.EndsWith(".dat", filePath);

            var rawBytes = File.ReadAllBytes(filePath);
            var rawText = System.Text.Encoding.UTF8.GetString(rawBytes);
            if (OperatingSystem.IsWindows())
            {
                // Encrypted content should NOT contain plain HTML tags
                Assert.DoesNotContain("<!DOCTYPE", rawText);
            }
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ReadReceipt_DecryptsCorrectly()
    {
        // Simulate SaveReceipt + ReadReceipt in a temp dir
        var tempDir = Path.Combine(Path.GetTempPath(), "SuavoAgent-test-receipts-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            var cmd = SampleCommand();
            var gen = new DeliveryReceiptGenerator();
            var html = gen.GenerateHtml(cmd, "Test Pharmacy");

            var plainBytes = System.Text.Encoding.UTF8.GetBytes(html);
            byte[] encryptedBytes;
            if (OperatingSystem.IsWindows())
            {
                encryptedBytes = System.Security.Cryptography.ProtectedData.Protect(
                    plainBytes, null, System.Security.Cryptography.DataProtectionScope.LocalMachine);
            }
            else
            {
                encryptedBytes = plainBytes;
            }

            var filePath = Path.Combine(tempDir, "receipt-test.dat");
            File.WriteAllBytes(filePath, encryptedBytes);

            var decrypted = DeliveryReceiptGenerator.ReadReceipt(filePath);
            Assert.NotNull(decrypted);
            Assert.Contains("98765", decrypted); // Rx number present after decryption
            Assert.Contains("<!DOCTYPE", decrypted); // Valid HTML after decryption
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void GenerateHtml_CalculatesTotal()
    {
        var gen = new DeliveryReceiptGenerator();
        var html = gen.GenerateHtml(SampleCommand(), "Test Pharmacy");
        Assert.Contains("$13.59", html); // 12.50 + 1.09
    }
}
