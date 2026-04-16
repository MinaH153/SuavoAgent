using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using SuavoAgent.Contracts.Models;

namespace SuavoAgent.Core.Receipts;

public sealed class DeliveryReceiptGenerator
{
    private static readonly byte[] FileMagic = "SVRC"u8.ToArray();
    private const byte FileVersion = 1;
    private static readonly int HeaderLength = FileMagic.Length + 1;

    private readonly ReceiptBranding _branding;

    public DeliveryReceiptGenerator(ReceiptBranding? branding = null)
    {
        _branding = branding ?? ReceiptBranding.Default;
    }

    public string GenerateHtml(DeliveryWritebackCommand cmd, string pharmacyName,
        string? driverName = null, string? proofImageBase64 = null)
    {
        var safePharmacy = E(pharmacyName);
        var safeCompany = E(_branding.CompanyName);
        var safeFooter = E(_branding.FooterText);
        var safeReceiptId = E(SafeHead(cmd.TaskId, 8));
        var safeRx = E(cmd.RxNumber);
        var safeFill = E(cmd.FillNumber.ToString());
        var safeSaleId = E(cmd.ExternalSaleId);
        var safeFirst = E(cmd.RecipientFirstName);
        var safeLast = E(cmd.RecipientLastName);
        var safeIdState = E(cmd.RecipientIdState);
        var safeDriver = E(driverName ?? "—");
        var safeMaskedId = E(MaskId(cmd.RecipientIdValue));

        var safePrimary = CssColor(_branding.PrimaryColor, ReceiptBranding.Default.PrimaryColor);
        var safeAccent = CssColor(_branding.AccentColor, ReceiptBranding.Default.AccentColor);
        var safeHeaderText = CssColor(_branding.HeaderTextColor, ReceiptBranding.Default.HeaderTextColor);

        var signatureHtml = string.IsNullOrEmpty(cmd.SignatureSvg)
            ? "<div class=\"signature-box\"><h4>Recipient Signature</h4><p class=\"no-sig\">No signature captured</p></div>"
            : $"<div class=\"signature-box\"><h4>Recipient Signature</h4>{SafeSvgSanitizer.Sanitize(cmd.SignatureSvg)}</div>";

        var proofHtml = string.IsNullOrEmpty(proofImageBase64) || !IsValidBase64(proofImageBase64)
            ? ""
            : $"<div class=\"proof-photo\"><h4>Proof of Delivery</h4><img src=\"data:image/jpeg;base64,{proofImageBase64}\" alt=\"Delivery proof\" /></div>";

        var counselingText = cmd.CounselingStatus switch
        {
            1 => "Accepted",
            2 => "Declined",
            3 => "Not Required",
            _ => "N/A"
        };
        var idTypeText = cmd.RecipientIdType switch
        {
            1 => "Driver License",
            2 => "State ID",
            _ => "Other"
        };

        var total = (cmd.Price + cmd.Tax).ToString("F2");
        var generatedAt = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");

        return $$"""
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8"/>
<title>Delivery Receipt — Rx #{{safeRx}}</title>
<style>
    * { margin: 0; padding: 0; box-sizing: border-box; }
    body { font-family: -apple-system, 'Segoe UI', Roboto, sans-serif; color: #1a1a1a; background: #f5f3ef; padding: 20px; }
    .receipt { max-width: 680px; margin: 0 auto; background: #fff; border-radius: 12px; box-shadow: 0 2px 12px rgba(0,0,0,0.08); overflow: hidden; }
    .header { background: {{safePrimary}}; color: {{safeHeaderText}}; padding: 24px 32px; display: flex; justify-content: space-between; align-items: center; }
    .header h1 { font-size: 20px; font-weight: 600; letter-spacing: 0.5px; }
    .header .receipt-id { font-size: 13px; opacity: 0.85; }
    .badge { background: {{safeAccent}}; color: #1a1a1a; padding: 4px 12px; border-radius: 20px; font-size: 12px; font-weight: 600; }
    .body { padding: 28px 32px; }
    .section { margin-bottom: 24px; }
    .section h3 { font-size: 11px; text-transform: uppercase; letter-spacing: 1.5px; color: #888; margin-bottom: 10px; }
    .detail-grid { display: grid; grid-template-columns: 1fr 1fr; gap: 12px; }
    .detail label { font-size: 11px; color: #999; display: block; margin-bottom: 2px; }
    .detail value { font-size: 15px; font-weight: 500; }
    .full-width { grid-column: 1 / -1; }
    .signature-box { border: 1px solid #e0ddd8; border-radius: 8px; padding: 16px; text-align: center; margin-top: 8px; }
    .signature-box h4 { font-size: 11px; text-transform: uppercase; letter-spacing: 1px; color: #888; margin-bottom: 8px; }
    .signature-box svg { max-width: 300px; max-height: 120px; }
    .no-sig { color: #ccc; font-style: italic; }
    .proof-photo { margin-top: 16px; text-align: center; }
    .proof-photo h4 { font-size: 11px; text-transform: uppercase; letter-spacing: 1px; color: #888; margin-bottom: 8px; }
    .proof-photo img { max-width: 100%; max-height: 300px; border-radius: 8px; border: 1px solid #e0ddd8; }
    .divider { height: 1px; background: #e0ddd8; margin: 20px 0; }
    .footer { background: #faf9f7; padding: 16px 32px; font-size: 11px; color: #999; text-align: center; border-top: 1px solid #e0ddd8; }
    .footer .legal { margin-top: 4px; font-size: 10px; }
    .amount { font-size: 22px; font-weight: 700; color: {{safePrimary}}; }
    @media print {
        body { background: #fff; padding: 0; }
        .receipt { box-shadow: none; border-radius: 0; }
    }
</style>
</head>
<body>
<div class="receipt">
    <div class="header">
        <div>
            <h1>{{safeCompany}}</h1>
            <div class="receipt-id">Receipt #{{safeReceiptId}}</div>
        </div>
        <span class="badge">DELIVERED</span>
    </div>
    <div class="body">
        <div class="section">
            <h3>Pharmacy</h3>
            <div class="detail"><label>Name</label><value>{{safePharmacy}}</value></div>
        </div>
        <div class="section">
            <h3>Prescription</h3>
            <div class="detail-grid">
                <div class="detail"><label>Rx Number</label><value>{{safeRx}}</value></div>
                <div class="detail"><label>Fill Number</label><value>{{safeFill}}</value></div>
                <div class="detail"><label>Counseling</label><value>{{counselingText}}</value></div>
                <div class="detail"><label>Sale ID</label><value>{{safeSaleId}}</value></div>
            </div>
        </div>
        <div class="section">
            <h3>Recipient</h3>
            <div class="detail-grid">
                <div class="detail"><label>Name</label><value>{{safeFirst}} {{safeLast}}</value></div>
                <div class="detail"><label>ID Type</label><value>{{idTypeText}}</value></div>
                <div class="detail"><label>ID Number</label><value>{{safeMaskedId}}</value></div>
                <div class="detail"><label>ID State</label><value>{{safeIdState}}</value></div>
            </div>
        </div>
        <div class="divider"></div>
        <div class="section">
            <h3>Delivery</h3>
            <div class="detail-grid">
                <div class="detail"><label>Delivered At</label><value>{{cmd.DeliveredAt.ToString("MMM dd, yyyy h:mm tt")}}</value></div>
                <div class="detail"><label>Driver</label><value>{{safeDriver}}</value></div>
                <div class="detail"><label>Subtotal</label><value>${{cmd.Price.ToString("F2")}}</value></div>
                <div class="detail"><label>Tax</label><value>${{cmd.Tax.ToString("F2")}}</value></div>
                <div class="detail full-width" style="text-align:right;margin-top:8px;">
                    <label>Total</label><span class="amount">${{total}}</span>
                </div>
            </div>
        </div>
        <div class="divider"></div>
        {{signatureHtml}}
        {{proofHtml}}
    </div>
    <div class="footer">
        <div>{{safeFooter}}</div>
        <div class="legal">This document serves as proof of delivery for audit and compliance purposes.
        Generated by SuavoAgent • {{generatedAt}} UTC</div>
    </div>
</div>
</body>
</html>
""";
    }

    /// <summary>
    /// Atomically persists an encrypted receipt. Filename hashes the Rx to avoid
    /// PHI in the directory listing; a GUID suffix prevents same-timestamp collisions.
    /// </summary>
    public string SaveReceipt(DeliveryWritebackCommand cmd, string pharmacyName,
        string? driverName = null, string? proofImageBase64 = null)
    {
        var receiptsDir = ReceiptsDirectory();
        Directory.CreateDirectory(receiptsDir);

        var html = GenerateHtml(cmd, pharmacyName, driverName, proofImageBase64);

        var plain = Encoding.UTF8.GetBytes(html);
        var cipher = OperatingSystem.IsWindows()
            ? ProtectedData.Protect(plain, null, DataProtectionScope.LocalMachine)
            : plain;

        var framed = new byte[HeaderLength + cipher.Length];
        Buffer.BlockCopy(FileMagic, 0, framed, 0, FileMagic.Length);
        framed[FileMagic.Length] = FileVersion;
        Buffer.BlockCopy(cipher, 0, framed, HeaderLength, cipher.Length);

        var rxHash = HashForFilename(cmd.RxNumber);
        var timestamp = cmd.DeliveredAt.ToString("yyyyMMdd-HHmmssfffffff");
        var uniq = Guid.NewGuid().ToString("N").AsSpan(0, 8).ToString();
        var fileName = $"receipt-{rxHash}-{timestamp}-{uniq}.dat";
        var filePath = Path.Combine(receiptsDir, fileName);

        WriteAtomically(filePath, framed);
        return filePath;
    }

    /// <summary>
    /// Decrypts the receipt, returning null on magic mismatch, truncation, decrypt
    /// failure, or encoding corruption. Never throws on disk-level damage.
    /// </summary>
    public static string? ReadReceipt(string filePath)
    {
        if (!File.Exists(filePath)) return null;
        byte[] bytes;
        try { bytes = File.ReadAllBytes(filePath); }
        catch (IOException) { return null; }

        if (bytes.Length <= HeaderLength) return null;
        if (!bytes.AsSpan(0, FileMagic.Length).SequenceEqual(FileMagic)) return null;
        if (bytes[FileMagic.Length] != FileVersion) return null;

        var cipher = bytes.AsSpan(HeaderLength).ToArray();

        try
        {
            var plain = OperatingSystem.IsWindows()
                ? ProtectedData.Unprotect(cipher, null, DataProtectionScope.LocalMachine)
                : cipher;
            return Encoding.UTF8.GetString(plain);
        }
        catch (CryptographicException) { return null; }
        catch (DecoderFallbackException) { return null; }
    }

    public static int PurgeExpiredReceipts(int retentionDays, Microsoft.Extensions.Logging.ILogger? logger = null)
    {
        var receiptsDir = ReceiptsDirectory();
        if (!Directory.Exists(receiptsDir)) return 0;

        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
        var purged = 0;
        foreach (var file in Directory.GetFiles(receiptsDir, "receipt-*.dat"))
        {
            var fileInfo = new FileInfo(file);
            if (fileInfo.CreationTimeUtc >= cutoff) continue;
            try
            {
                File.Delete(file);
                purged++;
                logger?.LogInformation("Purged expired receipt: {FileName} (created {Created})",
                    fileInfo.Name, fileInfo.CreationTimeUtc);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Failed to purge receipt: {FileName}", fileInfo.Name);
            }
        }
        return purged;
    }

    private static string ReceiptsDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "SuavoAgent", "delivery-receipts");

    private static void WriteAtomically(string filePath, byte[] bytes)
    {
        var dir = Path.GetDirectoryName(filePath) ?? ".";
        var tempPath = Path.Combine(dir, $".{Path.GetFileName(filePath)}.tmp");
        using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(bytes, 0, bytes.Length);
            fs.Flush(flushToDisk: true);
        }

        if (File.Exists(filePath))
        {
            File.Replace(tempPath, filePath, destinationBackupFileName: null, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(tempPath, filePath);
        }
    }

    private static string HashForFilename(string rxNumber)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(rxNumber ?? ""));
        return Convert.ToHexString(bytes).AsSpan(0, 16).ToString().ToLowerInvariant();
    }

    private static string SafeHead(string? value, int count)
    {
        if (string.IsNullOrEmpty(value)) return "";
        return value.Length <= count ? value : value[..count];
    }

    private static string MaskId(string idValue)
    {
        if (string.IsNullOrEmpty(idValue) || idValue.Length < 4) return "****";
        return new string('*', idValue.Length - 4) + idValue[^4..];
    }

    private static string E(string? value) => string.IsNullOrEmpty(value) ? "" : WebUtility.HtmlEncode(value);

    private static bool IsValidBase64(string value)
    {
        if (value.Length > 5_000_000) return false; // ~3.7 MB decoded — hard cap
        foreach (var c in value)
        {
            var ok = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')
                || (c >= '0' && c <= '9') || c == '+' || c == '/' || c == '=';
            if (!ok) return false;
        }
        return true;
    }

    private static string CssColor(string? candidate, string fallback)
    {
        if (string.IsNullOrEmpty(candidate)) return fallback;
        if (candidate.Length > 32) return fallback;
        foreach (var c in candidate)
        {
            var ok = char.IsLetterOrDigit(c) || c == '#' || c == '(' || c == ')' || c == ',' || c == ' ' || c == '.' || c == '%';
            if (!ok) return fallback;
        }
        return candidate;
    }
}

/// <summary>
/// Customizable receipt branding. Fleet operators can override per-pharmacy.
/// </summary>
public sealed class ReceiptBranding
{
    public string CompanyName { get; init; } = "Suavo";
    public string PrimaryColor { get; init; } = "#2c2c2c";
    public string AccentColor { get; init; } = "#d4a853";
    public string HeaderTextColor { get; init; } = "#f5f0e8";
    public string FooterText { get; init; } = "Suavo Delivery Services • suavollc.com";

    public static readonly ReceiptBranding Default = new();
}

/// <summary>
/// Allowlist-based SVG sanitizer. Parses input as XML with DTD processing disabled,
/// walks the tree, and emits only a whitelisted subset of elements and attributes.
/// Rejects the signature entirely on any parse error — fail-closed.
/// </summary>
internal static class SafeSvgSanitizer
{
    private const string SvgNs = "http://www.w3.org/2000/svg";

    private static readonly HashSet<string> AllowedElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "svg", "g", "path", "polyline", "polygon", "line",
        "circle", "ellipse", "rect", "text", "tspan", "title", "desc"
    };

    private static readonly HashSet<string> AllowedAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "viewBox", "width", "height", "d", "points",
        "x", "y", "x1", "y1", "x2", "y2",
        "cx", "cy", "r", "rx", "ry",
        "stroke", "fill", "stroke-width", "stroke-linecap", "stroke-linejoin",
        "stroke-dasharray", "fill-rule", "fill-opacity", "stroke-opacity",
        "transform", "font-family", "font-size", "text-anchor"
    };

    public static string Sanitize(string svg)
    {
        if (string.IsNullOrWhiteSpace(svg)) return "";
        if (svg.Length > 200_000) return "";

        XDocument doc;
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersFromEntities = 0,
                MaxCharactersInDocument = 200_000,
            };
            using var reader = XmlReader.Create(new StringReader(svg), settings);
            doc = XDocument.Load(reader);
        }
        catch { return ""; }

        if (doc.Root == null || !string.Equals(doc.Root.Name.LocalName, "svg", StringComparison.OrdinalIgnoreCase))
            return "";

        var clean = Rebuild(doc.Root);
        return clean?.ToString(SaveOptions.DisableFormatting) ?? "";
    }

    private static XElement? Rebuild(XElement source)
    {
        if (!AllowedElements.Contains(source.Name.LocalName)) return null;

        var localName = source.Name.LocalName.ToLowerInvariant();
        var element = new XElement(XName.Get(localName, SvgNs));

        foreach (var attr in source.Attributes())
        {
            if (attr.IsNamespaceDeclaration) continue;
            if (!AllowedAttributes.Contains(attr.Name.LocalName)) continue;
            if (!IsSafeAttributeValue(attr.Value)) continue;
            element.SetAttributeValue(XName.Get(attr.Name.LocalName.ToLowerInvariant()), attr.Value);
        }

        foreach (var node in source.Nodes())
        {
            if (node is XElement child)
            {
                var rebuilt = Rebuild(child);
                if (rebuilt != null) element.Add(rebuilt);
            }
            else if (node is XText text)
            {
                element.Add(new XText(text.Value));
            }
        }

        return element;
    }

    private static bool IsSafeAttributeValue(string? value)
    {
        if (string.IsNullOrEmpty(value)) return true;
        var trimmed = value.TrimStart();
        if (trimmed.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)) return false;
        if (trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return false;
        if (trimmed.StartsWith("vbscript:", StringComparison.OrdinalIgnoreCase)) return false;
        if (trimmed.Contains("expression(", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }
}
