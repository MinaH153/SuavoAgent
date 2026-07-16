using System.IO.Compression;
using System.Security.Cryptography;
using System.Xml.Linq;
using ClosedXML.Excel;

namespace SuavoAgent.Core.Pricing;

/// <summary>
/// A disposable private execution snapshot. The caller never reads or writes
/// the operator's live source after admission, closing source-file TOCTOU.
/// </summary>
internal sealed class PricingWorkbookExecutionLease : IDisposable
{
    private int _disposed;

    internal PricingWorkbookExecutionLease(
        string workbookPath,
        string sourceSha256,
        bool wasNormalized)
    {
        WorkbookPath = workbookPath;
        SourceSha256 = sourceSha256;
        WasNormalized = wasNormalized;
    }

    internal string WorkbookPath { get; }
    internal string SourceSha256 { get; }
    internal bool WasNormalized { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        try { File.Delete(WorkbookPath); } catch { /* best-effort local cleanup */ }
    }
}

internal static partial class PricingWorkbookContentPolicy
{
    // These two opaque wrapper parts are not added to the normal allow-list.
    // The sanitizer recognizes only the exact empty drawing and exact local
    // Google workbook-metadata cohort observed in Nadim's supplied export,
    // then discards both. Any byte change remains fail-closed.
    private const string NadimEmptyDrawingSha256 =
        "394373ea7e1b1dfd8633980cf12b48a29def82f40be77ecb084131e50739dc4b";
    private const string NadimGoogleMetadataSha256 =
        "c691b512de3f92205ca598eca4e946184375e7aa8ce443381b2016cd29ac2853";

    private static readonly HashSet<string> ExactGoogleWrapperParts = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "[Content_Types].xml",
        "_rels/.rels",
        "xl/workbook.xml",
        "xl/_rels/workbook.xml.rels",
        "xl/worksheets/sheet1.xml",
        "xl/worksheets/_rels/sheet1.xml.rels",
        "xl/drawings/drawing1.xml",
        "xl/sharedStrings.xml",
        "xl/styles.xml",
        "xl/theme/theme1.xml",
        "xl/metadata",
    };

    /// <summary>
    /// Strict execution admission with one bounded local sanitizer fallback for
    /// Nadim's exact Google-export wrapper. It never changes the source file.
    /// </summary>
    internal static bool TryPrepareForExecution(
        string path,
        out PricingWorkbookExecutionLease? lease,
        out string code)
    {
        lease = null;
        string? sourceSnapshot = null;
        string? normalizedPath = null;
        try
        {
            sourceSnapshot = CreatePrivateSourceSnapshot(path, out var sourceSha256);
            try
            {
                Validate(sourceSnapshot);
                lease = new PricingWorkbookExecutionLease(
                    sourceSnapshot, sourceSha256, wasNormalized: false);
                sourceSnapshot = null; // ownership transferred to the lease
                code = "ok_private_snapshot";
                return true;
            }
            catch (PricingWorkbookContentException ex) when (ex.Code is
                "xlsx_unsupported_part_forbidden" or "xlsx_pricing_schema_forbidden")
            {
                // Only these two failures can represent the known Google wrapper.
            }

            normalizedPath = NormalizeExactGoogleWrapper(sourceSnapshot!);
            // The produced artifact gets no special treatment: the existing
            // strict policy must admit it before either runner can use it.
            Validate(normalizedPath);
            lease = new PricingWorkbookExecutionLease(
                normalizedPath, sourceSha256, wasNormalized: true);
            normalizedPath = null; // ownership transferred to the lease
            code = "ok_normalized_google_export";
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
            code = "xlsx_google_wrapper_profile_forbidden";
            return false;
        }
        finally
        {
            DeleteBestEffort(sourceSnapshot);
            DeleteBestEffort(normalizedPath);
        }
    }

    internal static string CreatePrivateSourceSnapshot(
        string sourcePath,
        out string sha256)
    {
        var source = new FileInfo(sourcePath);
        if (!source.Exists || source.Length is <= 0 or > MaxArchiveBytes)
            throw new PricingWorkbookContentException("xlsx_archive_size_invalid");

        var snapshotPath = NewPrivateWorkbookPath("source");
        try
        {
            using (var input = new FileStream(
                       sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                       64 * 1024, FileOptions.SequentialScan))
            using (var output = new FileStream(
                       snapshotPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       64 * 1024, FileOptions.SequentialScan))
            {
                input.CopyTo(output);
                output.Flush(flushToDisk: true);
            }
            var snapshot = new FileInfo(snapshotPath);
            if (snapshot.Length is <= 0 or > MaxArchiveBytes)
                throw new PricingWorkbookContentException("xlsx_archive_size_invalid");
            using var digestInput = new FileStream(
                snapshotPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            sha256 = Convert.ToHexString(SHA256.HashData(digestInput)).ToLowerInvariant();
            return snapshotPath;
        }
        catch
        {
            DeleteBestEffort(snapshotPath);
            throw;
        }
    }

    private static string NormalizeExactGoogleWrapper(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length is <= 0 or > MaxArchiveBytes)
            throw new PricingWorkbookContentException("xlsx_archive_size_invalid");

        IReadOnlyList<GooglePricingRow> rows;
        using (var stream = new FileStream(
                   path, FileMode.Open, FileAccess.Read, FileShare.Read,
                   64 * 1024, FileOptions.SequentialScan))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false))
        {
            var (entries, _) = ValidateArchiveEnvelope(archive);
            if (!ExactGoogleWrapperParts.SetEquals(entries.Keys))
                throw new PricingWorkbookContentException("xlsx_google_wrapper_profile_forbidden");

            if (entries.Keys.Any(IsForbiddenActivePart))
                throw new PricingWorkbookContentException("xlsx_active_content_forbidden");

            foreach (var pair in entries.Where(pair =>
                         pair.Key.Equals("[Content_Types].xml", StringComparison.OrdinalIgnoreCase) ||
                         pair.Key.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)))
                InspectRelationshipXml(pair.Value);
            InspectContentTypes(entries["[Content_Types].xml"]);

            // Scan every textual part before extracting any values. The two
            // opaque parts are admitted only by fixed digest and are discarded.
            foreach (var pair in entries.Where(pair =>
                         pair.Key.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ||
                         pair.Key.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)))
                InspectXmlTextAndAttributes(pair.Value);

            if (!EntrySha256(entries["xl/drawings/drawing1.xml"])
                    .Equals(NadimEmptyDrawingSha256, StringComparison.Ordinal) ||
                !EntrySha256(entries["xl/metadata"])
                    .Equals(NadimGoogleMetadataSha256, StringComparison.Ordinal))
                throw new PricingWorkbookContentException("xlsx_google_wrapper_profile_forbidden");

            ValidateExactGoogleRelationships(entries);
            rows = ExtractGooglePricingRows(entries);
        }

        var normalizedPath = NewPrivateWorkbookPath("normalized");
        try
        {
            using var workbook = new XLWorkbook();
            var worksheet = workbook.AddWorksheet("Pricing");
            worksheet.Cell(1, 1).Value = "Drug";
            worksheet.Cell(1, 2).Value = "Strength";
            worksheet.Cell(1, 3).Value = "NDC";
            for (var index = 0; index < rows.Count; index++)
            {
                var row = index + 2;
                worksheet.Cell(row, 1).SetValue(rows[index].Drug);
                worksheet.Cell(row, 2).SetValue(rows[index].Strength);
                worksheet.Cell(row, 3).SetValue(rows[index].Ndc)
                    .Style.NumberFormat.Format = "@";
            }
            workbook.SaveAs(normalizedPath);
            return normalizedPath;
        }
        catch
        {
            DeleteBestEffort(normalizedPath);
            throw;
        }
    }

    private static void ValidateExactGoogleRelationships(
        IReadOnlyDictionary<string, ZipArchiveEntry> entries)
    {
        const string officeDocument =
            "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument";
        const string theme =
            "http://schemas.openxmlformats.org/officeDocument/2006/relationships/theme";
        const string styles =
            "http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles";
        const string sharedStrings =
            "http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings";
        const string worksheet =
            "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet";
        const string drawing =
            "http://schemas.openxmlformats.org/officeDocument/2006/relationships/drawing";
        const string metadata =
            "http://customschemas.google.com/relationships/workbookmetadata";

        var rootRelationships = ReadRelationships(entries["_rels/.rels"]);
        RequireRelationships(rootRelationships, [(officeDocument, "xl/workbook.xml")]);

        var workbookRelationships = ReadRelationships(entries["xl/_rels/workbook.xml.rels"]);
        RequireRelationships(workbookRelationships,
        [
            (theme, "theme/theme1.xml"),
            (styles, "styles.xml"),
            (sharedStrings, "sharedStrings.xml"),
            (worksheet, "worksheets/sheet1.xml"),
            (metadata, "metadata"),
        ]);

        var sheetRelationships = ReadRelationships(
            entries["xl/worksheets/_rels/sheet1.xml.rels"]);
        RequireRelationships(sheetRelationships, [(drawing, "../drawings/drawing1.xml")]);

        var contentTypes = LoadDocument(entries["[Content_Types].xml"]);
        RequireContentType(contentTypes, "/xl/metadata", "application/binary");
        RequireContentType(
            contentTypes,
            "/xl/drawings/drawing1.xml",
            "application/vnd.openxmlformats-officedocument.drawing+xml");

        XNamespace main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        XNamespace rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        var workbook = LoadDocument(entries["xl/workbook.xml"]);
        var sheets = workbook.Descendants(main + "sheet").ToArray();
        if (sheets.Length != 1 ||
            !string.Equals(sheets[0].Attribute("name")?.Value, "Sheet1", StringComparison.Ordinal) ||
            workbook.Descendants(main + "definedName").Any(element =>
                !string.IsNullOrWhiteSpace(element.Value)))
            throw new PricingWorkbookContentException("xlsx_google_wrapper_profile_forbidden");

        var metadataRelationship = workbookRelationships.Single(item => item.Type == metadata);
        var customData = workbook.Descendants()
            .SingleOrDefault(element => element.Name.LocalName == "sheetsCustomData");
        var extension = customData?.Parent;
        if (customData is null || extension is null ||
            !string.Equals(
                extension.Attribute("uri")?.Value,
                "GoogleSheetsCustomDataVersion2",
                StringComparison.Ordinal) ||
            !string.Equals(
                customData.Attribute(rel + "id")?.Value,
                metadataRelationship.Id,
                StringComparison.Ordinal))
            throw new PricingWorkbookContentException("xlsx_google_wrapper_profile_forbidden");
    }

    private static IReadOnlyList<GooglePricingRow> ExtractGooglePricingRows(
        IReadOnlyDictionary<string, ZipArchiveEntry> entries)
    {
        XNamespace main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var sharedStringsDocument = LoadDocument(entries["xl/sharedStrings.xml"]);
        var sharedStrings = sharedStringsDocument
            .Descendants(main + "si")
            .Select(item => string.Concat(item.Descendants(main + "t").Select(text => text.Value)))
            .ToArray();

        var sheet = LoadDocument(entries["xl/worksheets/sheet1.xml"]);
        if (sheet.Descendants(main + "f").Any())
            throw new PricingWorkbookContentException("xlsx_formula_forbidden");

        var parsedRows = sheet.Descendants(main + "row")
            .Select(row => ReadSourceRow(row, sharedStrings, main))
            .ToArray();
        var header = parsedRows.FirstOrDefault(row =>
            row.RowNumber is >= 1 and <= MaxHeaderScanRows &&
            row.Cells.Values.Any(value => NormalizeHeader(value) is "NDC"));
        if (header is null)
            throw new PricingWorkbookContentException("xlsx_google_report_shape_forbidden");

        var ndcColumn = SingleHeaderColumn(header, "NDC");
        var drugColumn = SingleHeaderColumn(header, "Drug");
        var strengthColumn = SingleHeaderColumn(header, "Strength");
        var output = new List<GooglePricingRow>();
        var ndcs = new HashSet<string>(StringComparer.Ordinal);

        foreach (var sourceRow in parsedRows.Where(row => row.RowNumber > header.RowNumber))
        {
            var rawNdc = sourceRow.Cells.GetValueOrDefault(ndcColumn)?.Trim() ?? "";
            var drug = sourceRow.Cells.GetValueOrDefault(drugColumn)?.Trim() ?? "";
            var strength = sourceRow.Cells.GetValueOrDefault(strengthColumn)?.Trim() ?? "";
            if (rawNdc.Length == 0)
            {
                // Preserve a populated report row with a missing NDC in the
                // clean artifact. The strict reader will classify it as an
                // invalid/manual-review row instead of silently erasing it.
                // Blank page furniture has neither Drug nor Strength and is
                // intentionally omitted.
                if (drug.Length == 0 && strength.Length == 0)
                    continue;
                if (drug.Length > 256 || strength.Length > 128)
                    throw new PricingWorkbookContentException(
                        "xlsx_google_report_shape_forbidden");
                output.Add(new GooglePricingRow(drug, strength, ""));
                if (output.Count > 500)
                    throw new PricingWorkbookContentException(
                        "xlsx_google_report_shape_forbidden");
                continue;
            }
            if (NormalizeHeader(rawNdc) == "NDC")
            {
                if (NormalizeHeader(sourceRow.Cells.GetValueOrDefault(drugColumn) ?? "") != "Drug" ||
                    NormalizeHeader(sourceRow.Cells.GetValueOrDefault(strengthColumn) ?? "") != "Strength")
                    throw new PricingWorkbookContentException("xlsx_google_report_shape_forbidden");
                continue;
            }

            var normalized = NdcNormalizer.Normalize(rawNdc);
            if (!normalized.Ok || normalized.Canonical11 is null ||
                !ndcs.Add(normalized.Canonical11))
                throw new PricingWorkbookContentException("xlsx_google_report_shape_forbidden");

            if (drug.Length is < 1 or > 256 || strength.Length > 128)
                throw new PricingWorkbookContentException("xlsx_google_report_shape_forbidden");

            output.Add(new GooglePricingRow(drug, strength, normalized.Canonical11));
            if (output.Count > 500)
                throw new PricingWorkbookContentException("xlsx_google_report_shape_forbidden");
        }

        if (output.Count == 0)
            throw new PricingWorkbookContentException("xlsx_google_report_shape_forbidden");
        return output;
    }

    private static GoogleSourceRow ReadSourceRow(
        XElement row,
        IReadOnlyList<string> sharedStrings,
        XNamespace main)
    {
        if (!int.TryParse(row.Attribute("r")?.Value, out var rowNumber) || rowNumber < 1)
            throw new PricingWorkbookContentException("xlsx_google_report_shape_forbidden");

        var cells = new Dictionary<int, string>();
        foreach (var cell in row.Elements(main + "c"))
        {
            var column = ParseColumnNumber(cell.Attribute("r")?.Value);
            var value = ReadCellValue(cell, sharedStrings, main);
            if (!cells.TryAdd(column, value))
                throw new PricingWorkbookContentException("xlsx_google_report_shape_forbidden");
        }
        return new GoogleSourceRow(rowNumber, cells);
    }

    private static string ReadCellValue(
        XElement cell,
        IReadOnlyList<string> sharedStrings,
        XNamespace main)
    {
        var type = cell.Attribute("t")?.Value;
        if (type == "inlineStr")
            return string.Concat(cell.Descendants(main + "t").Select(text => text.Value));

        var value = cell.Element(main + "v")?.Value ?? "";
        if (type != "s")
            return value;
        if (!int.TryParse(value, out var index) || index < 0 || index >= sharedStrings.Count)
            throw new PricingWorkbookContentException("xlsx_google_report_shape_forbidden");
        return sharedStrings[index];
    }

    private static int ParseColumnNumber(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
            throw new PricingWorkbookContentException("xlsx_google_report_shape_forbidden");
        var column = 0;
        var letters = 0;
        foreach (var character in reference)
        {
            if (!char.IsLetter(character))
                break;
            var upper = char.ToUpperInvariant(character);
            if (upper is < 'A' or > 'Z')
                throw new PricingWorkbookContentException("xlsx_google_report_shape_forbidden");
            column = checked(column * 26 + upper - 'A' + 1);
            letters++;
        }
        if (letters == 0 || column is < 1 or > 32)
            throw new PricingWorkbookContentException("xlsx_google_report_shape_forbidden");
        return column;
    }

    private static int SingleHeaderColumn(GoogleSourceRow header, string name)
    {
        var matches = header.Cells
            .Where(pair => NormalizeHeader(pair.Value) == name)
            .Select(pair => pair.Key)
            .ToArray();
        if (matches.Length != 1)
            throw new PricingWorkbookContentException("xlsx_google_report_shape_forbidden");
        return matches[0];
    }

    private static IReadOnlyList<GoogleRelationship> ReadRelationships(ZipArchiveEntry entry)
    {
        var document = LoadDocument(entry);
        var relationships = document.Root?.Elements()
            .Select(element => new GoogleRelationship(
                element.Attribute("Id")?.Value ?? "",
                element.Attribute("Type")?.Value ?? "",
                element.Attribute("Target")?.Value ?? "",
                element.Attribute("TargetMode")?.Value))
            .ToArray() ?? [];
        if (relationships.Any(item =>
                item.Id.Length == 0 || item.Type.Length == 0 || item.Target.Length == 0 ||
                item.TargetMode is not null))
            throw new PricingWorkbookContentException("xlsx_google_wrapper_profile_forbidden");
        return relationships;
    }

    private static void RequireRelationships(
        IReadOnlyList<GoogleRelationship> actual,
        IReadOnlyList<(string Type, string Target)> expected)
    {
        if (actual.Count != expected.Count ||
            expected.Any(item => actual.Count(candidate =>
                candidate.Type == item.Type && candidate.Target == item.Target) != 1) ||
            actual.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != actual.Count)
            throw new PricingWorkbookContentException("xlsx_google_wrapper_profile_forbidden");
    }

    private static void RequireContentType(
        XDocument document,
        string partName,
        string contentType)
    {
        var matches = document.Root?.Elements()
            .Where(element =>
                string.Equals(element.Attribute("PartName")?.Value, partName, StringComparison.Ordinal) &&
                string.Equals(element.Attribute("ContentType")?.Value, contentType, StringComparison.Ordinal))
            .Count() ?? 0;
        if (matches != 1)
            throw new PricingWorkbookContentException("xlsx_google_wrapper_profile_forbidden");
    }

    private static XDocument LoadDocument(ZipArchiveEntry entry) =>
        XDocument.Load(OpenXml(entry), System.Xml.Linq.LoadOptions.None);

    private static string EntrySha256(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void DeleteBestEffort(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try { File.Delete(path); } catch { }
    }

    private static string NewPrivateWorkbookPath(string kind)
    {
        var directory = Path.Combine(
            Path.GetTempPath(), "SuavoAgent", "pricing-snapshots");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{kind}-{Guid.NewGuid():N}.xlsx");
    }

    private sealed record GoogleRelationship(
        string Id,
        string Type,
        string Target,
        string? TargetMode);

    private sealed record GoogleSourceRow(
        int RowNumber,
        IReadOnlyDictionary<int, string> Cells);

    private sealed record GooglePricingRow(string Drug, string Strength, string Ndc);
}
