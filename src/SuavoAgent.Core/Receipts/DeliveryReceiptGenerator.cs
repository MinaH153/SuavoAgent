using SuavoAgent.Contracts.Models;

namespace SuavoAgent.Core.Receipts;

/// <summary>
/// Generates branded HTML delivery receipts for local audit storage.
/// Self-contained HTML with embedded CSS — opens in any browser, prints perfectly.
/// Signature SVG embedded inline. Zero external dependencies.
/// </summary>
public sealed class DeliveryReceiptGenerator
{
    private readonly ReceiptBranding _branding;

    public DeliveryReceiptGenerator(ReceiptBranding? branding = null)
    {
        _branding = branding ?? ReceiptBranding.Default;
    }

    /// <summary>
    /// Generates an HTML receipt for a completed delivery.
    /// Contains: Rx info, recipient, signature, delivery timestamp, driver info.
    /// </summary>
    public string GenerateHtml(DeliveryWritebackCommand cmd, string pharmacyName,
        string? driverName = null, string? proofImageBase64 = null)
    {
        var signatureHtml = !string.IsNullOrEmpty(cmd.SignatureSvg)
            ? $"<div class=\"signature-box\"><h4>Recipient Signature</h4>{cmd.SignatureSvg}</div>"
            : "<div class=\"signature-box\"><h4>Recipient Signature</h4><p class=\"no-sig\">No signature captured</p></div>";

        var proofHtml = !string.IsNullOrEmpty(proofImageBase64)
            ? $"<div class=\"proof-photo\"><h4>Proof of Delivery</h4><img src=\"data:image/jpeg;base64,{proofImageBase64}\" alt=\"Delivery proof\" /></div>"
            : "";

        var counselingText = cmd.CounselingStatus switch
        {
            1 => "Accepted",
            2 => "Declined",
            3 => "Not Required",
            _ => "N/A"
        };

        var idTypeText = cmd.RecipientIdType == 1 ? "Driver License"
            : cmd.RecipientIdType == 2 ? "State ID"
            : "Other";

        var total = (cmd.Price + cmd.Tax).ToString("F2");
        var generatedAt = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");

        return $$"""
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8"/>
<title>Delivery Receipt — Rx #{{cmd.RxNumber}}</title>
<style>
    * { margin: 0; padding: 0; box-sizing: border-box; }
    body { font-family: -apple-system, 'Segoe UI', Roboto, sans-serif; color: #1a1a1a; background: #f5f3ef; padding: 20px; }
    .receipt { max-width: 680px; margin: 0 auto; background: #fff; border-radius: 12px; box-shadow: 0 2px 12px rgba(0,0,0,0.08); overflow: hidden; }
    .header { background: {{_branding.PrimaryColor}}; color: {{_branding.HeaderTextColor}}; padding: 24px 32px; display: flex; justify-content: space-between; align-items: center; }
    .header h1 { font-size: 20px; font-weight: 600; letter-spacing: 0.5px; }
    .header .receipt-id { font-size: 13px; opacity: 0.85; }
    .badge { background: {{_branding.AccentColor}}; color: #1a1a1a; padding: 4px 12px; border-radius: 20px; font-size: 12px; font-weight: 600; }
    .body { padding: 28px 32px; }
    .section { margin-bottom: 24px; }
    .section h3 { font-size: 11px; text-transform: uppercase; letter-spacing: 1.5px; color: #888; margin-bottom: 10px; }
    .detail-grid { display: grid; grid-template-columns: 1fr 1fr; gap: 12px; }
    .detail { }
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
    .amount { font-size: 22px; font-weight: 700; color: {{_branding.PrimaryColor}}; }
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
            <h1>{{_branding.CompanyName}}</h1>
            <div class="receipt-id">Receipt #{{cmd.TaskId[..8]}}</div>
        </div>
        <span class="badge">DELIVERED</span>
    </div>
    <div class="body">
        <div class="section">
            <h3>Pharmacy</h3>
            <div class="detail"><label>Name</label><value>{{pharmacyName}}</value></div>
        </div>
        <div class="section">
            <h3>Prescription</h3>
            <div class="detail-grid">
                <div class="detail"><label>Rx Number</label><value>{{cmd.RxNumber}}</value></div>
                <div class="detail"><label>Fill Number</label><value>{{cmd.FillNumber}}</value></div>
                <div class="detail"><label>Counseling</label><value>{{counselingText}}</value></div>
                <div class="detail"><label>Sale ID</label><value>{{cmd.ExternalSaleId}}</value></div>
            </div>
        </div>
        <div class="section">
            <h3>Recipient</h3>
            <div class="detail-grid">
                <div class="detail"><label>Name</label><value>{{cmd.RecipientFirstName}} {{cmd.RecipientLastName}}</value></div>
                <div class="detail"><label>ID Type</label><value>{{idTypeText}}</value></div>
                <div class="detail"><label>ID Number</label><value>{{MaskId(cmd.RecipientIdValue)}}</value></div>
                <div class="detail"><label>ID State</label><value>{{cmd.RecipientIdState}}</value></div>
            </div>
        </div>
        <div class="divider"></div>
        <div class="section">
            <h3>Delivery</h3>
            <div class="detail-grid">
                <div class="detail"><label>Delivered At</label><value>{{cmd.DeliveredAt.ToString("MMM dd, yyyy h:mm tt")}}</value></div>
                <div class="detail"><label>Driver</label><value>{{driverName ?? "—"}}</value></div>
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
        <div>{{_branding.FooterText}}</div>
        <div class="legal">This document serves as proof of delivery for audit and compliance purposes.
        Generated by SuavoAgent v3.9 • {{generatedAt}} UTC</div>
    </div>
</div>
</body>
</html>
""";
    }

    /// <summary>
    /// Generates the receipt and saves to the delivery-receipts folder.
    /// Returns the file path.
    /// </summary>
    public string SaveReceipt(DeliveryWritebackCommand cmd, string pharmacyName,
        string? driverName = null, string? proofImageBase64 = null)
    {
        var receiptsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SuavoAgent", "delivery-receipts");
        Directory.CreateDirectory(receiptsDir);

        var html = GenerateHtml(cmd, pharmacyName, driverName, proofImageBase64);
        var fileName = $"receipt-{cmd.RxNumber}-{cmd.DeliveredAt:yyyyMMdd-HHmmss}.html";
        var filePath = Path.Combine(receiptsDir, fileName);
        File.WriteAllText(filePath, html);

        return filePath;
    }

    private static string MaskId(string idValue)
    {
        if (string.IsNullOrEmpty(idValue) || idValue.Length < 4)
            return "****";
        return new string('*', idValue.Length - 4) + idValue[^4..];
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
