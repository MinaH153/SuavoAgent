using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using ClosedXML.Excel;

namespace SuavoAgent.Core.Pricing;

internal sealed record PricingWorkbookValidation(
    string Sha256,
    long SizeBytes,
    int EntryCount,
    long UncompressedBytes);

internal sealed class PricingWorkbookContentException(string code) : IOException(code)
{
    public string Code { get; } = code;
}

/// <summary>
/// Native defense-in-depth for the web-validated pricing workbook. This is a
/// bounded active-content and DLP policy, not an antivirus scanner. It never
/// returns or logs workbook text.
/// </summary>
internal static partial class PricingWorkbookContentPolicy
{
    // Vercel functions cap request and response bodies at 4.5 MB. Keep the
    // workbook at 4 MiB so multipart and response framing retain headroom.
    internal const long MaxArchiveBytes = 4L * 1024 * 1024;
    private const long MaxEntryBytes = 16L * 1024 * 1024;
    private const long MaxExpandedBytes = 64L * 1024 * 1024;
    private const int MaxEntries = 512;
    private const int MaxHeaderScanRows = 25;
    private static readonly HashSet<string> ApprovedPricingHeaders = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "#", "Rank", "NDC", "NDC11", "Drug", "Drug Name", "Generic Name",
        "Brand Name", "Strength", "Dosage Form", "Total Dispensed",
        "Quantity", "Monthly Qty", "Acquisition Cost", "Current Cost",
        "Price", "WAC", "AWP", "Package Size", "Manufacturer", "RxBIN",
        "Supplier", "Best Supplier", "Cheapest Supplier", "Cost", "Cost (per unit)",
        "Best Cost", "Best Cost Per Unit", "Price Lookup Status",
    };
    private static readonly HashSet<string> ApprovedWorksheetNames = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "Pricing", "Sheet1", "Top 500", "Top 500 Most Dispensed Rx Items",
    };

    [GeneratedRegex(@"\b(?:patient|dob|date\s*of\s*birth|address|phone|telephone|diagnosis)\b|\b(?:patient|customer|member)\s*(?:full\s*)?name\b|\b(?:rx|prescription)\s*(?:(?:number|no)\b|#)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PhiFieldRegex();

    internal static PricingWorkbookValidation Validate(string path)
    {
        var validation = ValidateArchiveSafety(path);
        ValidatePricingSchema(path);
        return validation;
    }

    /// <summary>
    /// Validates only the bounded XLSX/archive security envelope shared by the
    /// separate Feature-A and Feature-B schema gates. Callers must apply their
    /// own exact schema validation before using any workbook values.
    /// </summary>
    internal static PricingWorkbookValidation ValidateArchiveSafety(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length is <= 0 or > MaxArchiveBytes)
            throw new PricingWorkbookContentException("xlsx_archive_size_invalid");

        var digest = ComputeSha256(path);
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.SequentialScan);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        var (entries, expanded) = ValidateArchiveEnvelope(archive);

        foreach (var required in new[]
        {
            "[Content_Types].xml", "_rels/.rels", "xl/workbook.xml",
            "xl/_rels/workbook.xml.rels",
        })
        {
            if (!entries.ContainsKey(required))
                throw new PricingWorkbookContentException("xlsx_required_part_missing");
        }
        if (!entries.Keys.Any(name =>
                Regex.IsMatch(name, @"^xl/worksheets/sheet\d+\.xml$", RegexOptions.IgnoreCase)))
            throw new PricingWorkbookContentException("xlsx_worksheet_missing");

        if (entries.Keys.Any(IsForbiddenActivePart))
            throw new PricingWorkbookContentException("xlsx_active_content_forbidden");
        if (entries.Keys.Any(name => !IsAllowedPricingPart(name)))
            throw new PricingWorkbookContentException("xlsx_unsupported_part_forbidden");

        foreach (var pair in entries.Where(pair =>
                     pair.Key.Equals("[Content_Types].xml", StringComparison.OrdinalIgnoreCase) ||
                     pair.Key.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)))
            InspectRelationshipXml(pair.Value);

        InspectContentTypes(entries["[Content_Types].xml"]);
        // DLP is an archive-wide boundary, not a worksheet-only heuristic.
        // Comments, threaded comments, custom properties, defined names, table
        // metadata, and vendor extensions can all carry user-controlled text.
        foreach (var pair in entries.Where(pair =>
                     pair.Key.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ||
                     pair.Key.EndsWith(".rels", StringComparison.OrdinalIgnoreCase) ||
                     pair.Key.EndsWith(".psmdcp", StringComparison.OrdinalIgnoreCase)))
            InspectXmlTextAndAttributes(pair.Value);
        foreach (var pair in entries.Where(pair =>
                     Regex.IsMatch(
                         pair.Key,
                         @"^xl/worksheets/sheet\d+\.xml$",
                         RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)))
            InspectWorksheetForFormulas(pair.Value);

        return new PricingWorkbookValidation(digest, file.Length, entries.Count, expanded);
    }

    /// <summary>
    /// Applies the archive path, count, expansion, and compression bounds shared
    /// by strict validation and the exact Google-export sanitizer.
    /// </summary>
    private static (Dictionary<string, ZipArchiveEntry> Entries, long ExpandedBytes)
        ValidateArchiveEnvelope(ZipArchive archive)
    {
        if (archive.Entries.Count is <= 0 or > MaxEntries)
            throw new PricingWorkbookContentException("xlsx_entry_count_invalid");

        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        foreach (var entry in archive.Entries)
        {
            var name = ValidateName(entry.FullName);
            if (!entries.TryAdd(name, entry))
                throw new PricingWorkbookContentException("xlsx_duplicate_entry");
            if (entry.Length < 0 || entry.Length > MaxEntryBytes || entry.CompressedLength < 0)
                throw new PricingWorkbookContentException("xlsx_entry_bounds_invalid");
            expanded = checked(expanded + entry.Length);
            if (expanded > MaxExpandedBytes)
                throw new PricingWorkbookContentException("xlsx_expansion_limit_exceeded");
            if (entry.CompressedLength > 0 && entry.Length / (double)entry.CompressedLength > 200d)
                throw new PricingWorkbookContentException("xlsx_compression_ratio_exceeded");
        }
        return (entries, expanded);
    }

    /// <summary>
    /// Shared fail-closed execution gate used by both SQL and UIA runners. All
    /// entry paths converge on one of those runners, so direct, discovered,
    /// scheduled, and cloud-uploaded workbooks receive the same native policy.
    /// </summary>
    internal static bool TryValidateForExecution(string path, out string code)
    {
        try
        {
            Validate(path);
            code = "ok";
            return true;
        }
        catch (PricingWorkbookContentException ex)
        {
            code = ex.Code;
            return false;
        }
        catch (Exception ex) when (ex is
            IOException or InvalidDataException or ArgumentException or FormatException)
        {
            code = "xlsx_pricing_schema_invalid";
            return false;
        }
    }

    private static string ComputeSha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string ValidateName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 240 || name.Contains('\0') ||
            name.Contains('\\') || name.StartsWith('/') || Path.IsPathRooted(name) ||
            name.Split('/').Any(segment => segment is "." or ".."))
            throw new PricingWorkbookContentException("xlsx_entry_path_invalid");
        return name;
    }

    private static bool IsForbiddenActivePart(string name)
    {
        var normalized = name.ToLowerInvariant();
        return normalized is "xl/vbaproject.bin" or "xl/connections.xml" ||
               normalized.StartsWith("xl/externallinks/", StringComparison.Ordinal) ||
               normalized.StartsWith("xl/embeddings/", StringComparison.Ordinal) ||
               normalized.StartsWith("xl/querytables/", StringComparison.Ordinal) ||
               normalized.StartsWith("customxml/", StringComparison.Ordinal) ||
               Regex.IsMatch(normalized, @"\.(exe|dll|com|scr|bat|cmd|ps1|js|vbs|msi|jar)$");
    }

    private static bool IsAllowedPricingPart(string name)
    {
        if (name.Equals("[Content_Types].xml", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("_rels/.rels", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("xl/workbook.xml", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("xl/_rels/workbook.xml.rels", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("xl/styles.xml", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("xl/sharedStrings.xml", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("xl/calcChain.xml", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("xl/metadata.xml", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("docProps/core.xml", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("docProps/app.xml", StringComparison.OrdinalIgnoreCase))
            return true;

        return Regex.IsMatch(
                   name,
                   @"^package/services/metadata/core-properties/[a-f0-9]{32}\.psmdcp$",
                   RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
               Regex.IsMatch(
                   name,
                   @"^xl/worksheets/sheet\d+\.xml$",
                   RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
               Regex.IsMatch(
                   name,
                   @"^xl/worksheets/_rels/sheet\d+\.xml\.rels$",
                   RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
               Regex.IsMatch(
                   name,
                   @"^xl/theme/theme\d+\.xml$",
                   RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
               Regex.IsMatch(
                   name,
                   @"^xl/tables/table\d+\.xml$",
                   RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static XmlReader OpenXml(ZipArchiveEntry entry)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxEntryBytes,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
        };
        return XmlReader.Create(entry.Open(), settings);
    }

    private static void InspectRelationshipXml(ZipArchiveEntry entry)
    {
        try
        {
            using var reader = OpenXml(entry);
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element) continue;
                var targetMode = reader.GetAttribute("TargetMode");
                var contentType = reader.GetAttribute("ContentType");
                var relationshipType = reader.GetAttribute("Type");
                var target = reader.GetAttribute("Target");
                if (string.Equals(targetMode, "External", StringComparison.OrdinalIgnoreCase) ||
                    contentType?.Contains("macroEnabled", StringComparison.OrdinalIgnoreCase) == true ||
                    contentType?.Contains("vbaProject", StringComparison.OrdinalIgnoreCase) == true ||
                    relationshipType?.Contains("oleObject", StringComparison.OrdinalIgnoreCase) == true ||
                    relationshipType?.EndsWith("/package", StringComparison.OrdinalIgnoreCase) == true ||
                    relationshipType?.Contains("externalLink", StringComparison.OrdinalIgnoreCase) == true ||
                    relationshipType?.Contains("attachedTemplate", StringComparison.OrdinalIgnoreCase) == true ||
                    relationshipType?.Contains("connections", StringComparison.OrdinalIgnoreCase) == true ||
                    relationshipType?.Contains("queryTable", StringComparison.OrdinalIgnoreCase) == true ||
                    target is not null && Regex.IsMatch(
                        target,
                        @"\.(exe|dll|com|scr|bat|cmd|ps1|js|vbs|msi|jar)(?:[?#].*)?$",
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    throw new PricingWorkbookContentException("xlsx_external_or_active_relationship_forbidden");
            }
        }
        catch (XmlException)
        {
            throw new PricingWorkbookContentException("xlsx_xml_invalid");
        }
    }

    private static void InspectContentTypes(ZipArchiveEntry entry)
    {
        try
        {
            var foundWorkbook = false;
            using var reader = OpenXml(entry);
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element) continue;
                var contentType = reader.GetAttribute("ContentType");
                if (contentType?.Contains("macroEnabled", StringComparison.OrdinalIgnoreCase) == true ||
                    contentType?.Contains("vbaProject", StringComparison.OrdinalIgnoreCase) == true ||
                    contentType?.Contains("oleObject", StringComparison.OrdinalIgnoreCase) == true)
                    throw new PricingWorkbookContentException("xlsx_active_content_forbidden");
                if (contentType?.Contains("spreadsheetml.sheet.main+xml", StringComparison.Ordinal) == true)
                    foundWorkbook = true;
            }
            if (!foundWorkbook)
                throw new PricingWorkbookContentException("xlsx_workbook_content_type_invalid");
        }
        catch (XmlException)
        {
            throw new PricingWorkbookContentException("xlsx_xml_invalid");
        }
    }

    private static void InspectXmlTextAndAttributes(ZipArchiveEntry entry)
    {
        try
        {
            var spaced = new StringBuilder();
            var joined = new StringBuilder();
            using var reader = OpenXml(entry);
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element && reader.HasAttributes)
                {
                    while (reader.MoveToNextAttribute())
                        AppendNormalized(reader.Value, spaced, joined);
                    reader.MoveToElement();
                }
                if (reader.NodeType == XmlNodeType.EndElement &&
                    reader.LocalName is "si" or "is" or "c" or "comment" or
                        "definedName" or "table" or "property")
                {
                    spaced.Append(' ');
                    joined.Append(' ');
                    continue;
                }
                if (reader.NodeType is not (XmlNodeType.Text or XmlNodeType.CDATA)) continue;
                AppendNormalized(reader.Value, spaced, joined);
            }
            if (PhiFieldRegex().IsMatch(spaced.ToString()) ||
                PhiFieldRegex().IsMatch(joined.ToString()))
                throw new PricingWorkbookContentException("xlsx_phi_field_forbidden");
        }
        catch (XmlException)
        {
            throw new PricingWorkbookContentException("xlsx_xml_invalid");
        }
    }

    private static void InspectWorksheetForFormulas(ZipArchiveEntry entry)
    {
        try
        {
            using var reader = OpenXml(entry);
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "f")
                    throw new PricingWorkbookContentException("xlsx_formula_forbidden");
            }
        }
        catch (PricingWorkbookContentException)
        {
            throw;
        }
        catch (XmlException)
        {
            throw new PricingWorkbookContentException("xlsx_xml_invalid");
        }
    }

    private static void AppendNormalized(
        string value,
        StringBuilder spaced,
        StringBuilder joined)
    {
        var normalized = value.Replace('_', ' ').Replace('-', ' ').Replace('.', ' ');
        spaced.Append(' ').Append(normalized);
        joined.Append(normalized);
    }

    private static void ValidatePricingSchema(string path)
    {
        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.SequentialScan);
            using var workbook = new XLWorkbook(stream);
            if (workbook.Worksheets.Count != 1)
                throw new PricingWorkbookContentException("xlsx_pricing_schema_forbidden");
            var worksheet = workbook.Worksheets.Single();
            if (!ApprovedWorksheetNames.Contains(worksheet.Name))
                throw new PricingWorkbookContentException("xlsx_pricing_schema_forbidden");

            var lastRow = worksheet.LastRowUsed()?.RowNumber() ?? 0;
            var lastColumn = worksheet.LastColumnUsed()?.ColumnNumber() ?? 0;
            if (lastRow < 2 || lastColumn is < 1 or > 32)
                throw new PricingWorkbookContentException("xlsx_pricing_schema_forbidden");

            var headerRow = -1;
            for (var row = 1; row <= Math.Min(lastRow, MaxHeaderScanRows); row++)
            {
                if (Enumerable.Range(1, lastColumn).Any(column =>
                        IsNdcHeader(worksheet.Cell(row, column).GetString())))
                {
                    headerRow = row;
                    break;
                }
            }
            if (headerRow < 1)
                throw new PricingWorkbookContentException("xlsx_pricing_schema_forbidden");

            var ndcIdentityCount = Enumerable.Range(1, lastColumn).Count(column =>
                IsNdcHeader(worksheet.Cell(headerRow, column).GetString()));
            if (ndcIdentityCount != 1)
                throw new PricingWorkbookContentException("xlsx_ndc_identity_ambiguous");

            for (var column = 1; column <= lastColumn; column++)
            {
                var header = NormalizeHeader(worksheet.Cell(headerRow, column).GetString());
                if (!ApprovedPricingHeaders.Contains(header))
                    throw new PricingWorkbookContentException("xlsx_pricing_schema_forbidden");
            }
        }
        catch (PricingWorkbookContentException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException)
        {
            throw new PricingWorkbookContentException("xlsx_pricing_schema_invalid");
        }
    }

    private static bool IsNdcHeader(string value) =>
        NormalizeHeader(value) is "NDC" or "NDC11";

    private static string NormalizeHeader(string value) =>
        Regex.Replace(value.Trim(), @"\s+", " ");
}
