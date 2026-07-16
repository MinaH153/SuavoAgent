namespace SuavoAgent.Core.Pricing;

/// <summary>
/// Owns the private, immutable source snapshot used by the strict Feature-B workbook reader.
/// Disposing the lease removes the snapshot; the operator's source file is never mutated.
/// </summary>
public sealed class PreferredNdcWorkbookLease : IDisposable
{
    private readonly PricingWorkbookExecutionLease _snapshot;

    internal PreferredNdcWorkbookLease(
        PricingWorkbookExecutionLease snapshot,
        ExcelPreferredNdcReader reader)
    {
        _snapshot = snapshot;
        Reader = reader;
    }

    public string SourceSha256 => _snapshot.SourceSha256;
    public ExcelPreferredNdcReader Reader { get; }

    public void Dispose() => _snapshot.Dispose();
}

/// <summary>
/// Dedicated Feature-B XLSX admission. It first makes a private snapshot, then applies the bounded ZIP,
/// DLP, formula, external-link, and active-content policy, and finally requires the exact Feature-B
/// worksheet and headers. It has no Google-wrapper sanitizer and never falls back to lenient matching.
/// </summary>
public static class PreferredNdcWorkbookAdmission
{
    public static bool TryAdmit(
        string sourcePath,
        out PreferredNdcWorkbookLease? lease,
        out string code)
    {
        lease = null;
        string? snapshotPath = null;
        try
        {
            snapshotPath = PricingWorkbookContentPolicy.CreatePrivateSourceSnapshot(
                sourcePath,
                out var sourceSha256);
            PricingWorkbookContentPolicy.ValidateArchiveSafety(snapshotPath);
            var reader = ExcelPreferredNdcReader.LoadAdmittedSnapshot(snapshotPath);

            var snapshot = new PricingWorkbookExecutionLease(
                snapshotPath,
                sourceSha256,
                wasNormalized: false);
            snapshotPath = null;
            lease = new PreferredNdcWorkbookLease(snapshot, reader);
            code = "ok_private_snapshot";
            return true;
        }
        catch (PricingWorkbookContentException ex)
        {
            code = ex.Code;
            return false;
        }
        catch (Exception ex) when (ex is
            IOException or InvalidDataException or ArgumentException or FormatException or OverflowException)
        {
            code = "xlsx_preferred_ndc_schema_invalid";
            return false;
        }
        finally
        {
            if (snapshotPath is not null)
            {
                try { File.Delete(snapshotPath); } catch { /* private temp cleanup is best effort */ }
            }
        }
    }
}
