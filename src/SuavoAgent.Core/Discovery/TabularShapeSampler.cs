using System.Text;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using SuavoAgent.Contracts.Discovery;

namespace SuavoAgent.Core.Discovery;

/// <summary>
/// Samples tabular files (.xlsx, .csv, .tsv) — reads column headers and
/// a handful of data rows, detects which column matches the spec's
/// <see cref="ExpectedColumnPattern"/>, reports row count, flags
/// whether headers match the tabular hint list.
///
/// <para>
/// Note: only the modern OOXML <c>.xlsx</c> path is supported via ClosedXML —
/// the legacy binary <c>.xls</c> format is not. Packs must not list
/// <c>.xls</c> in <c>CommonExtensions</c> until we wire a different
/// reader (NPOI or similar).
/// </para>
///
/// <para>
/// Non-locking read: opens with <see cref="FileShare.ReadWrite"/> so
/// sampling a file while it's open in Excel doesn't lock it.
/// </para>
///
/// <para>
/// Cancellation: <see cref="OperationCanceledException"/> propagates
/// verbatim so callers can reliably abort discovery. Other exceptions
/// convert to <see cref="FileCandidateSample.ErrorMessage"/> entries.
/// </para>
/// </summary>
public sealed class TabularShapeSampler : IFileShapeSampler
{
    private const int SampleDepth = 20;

    private readonly ILogger<TabularShapeSampler>? _logger;

    public TabularShapeSampler(ILogger<TabularShapeSampler>? logger = null)
    {
        _logger = logger;
    }

    public async Task<FileCandidateSample> SampleAsync(
        FileCandidate candidate,
        FileDiscoverySpec spec,
        CancellationToken ct)
    {
        if (!File.Exists(candidate.AbsolutePath))
        {
            return new FileCandidateSample(candidate, Shape: null, ErrorMessage: "File not found at sample time.");
        }

        var ext = Path.GetExtension(candidate.FileName).ToLowerInvariant();
        var tabular = spec.Shape as TabularExpectation;

        SheetReadResult read;
        try
        {
            read = ext switch
            {
                ".xlsx" => await Task.Run(() => ReadExcel(candidate.AbsolutePath, ct), ct),
                ".csv" => await Task.Run(() => ReadDelimited(candidate.AbsolutePath, ',', ct), ct),
                ".tsv" => await Task.Run(() => ReadDelimited(candidate.AbsolutePath, '\t', ct), ct),
                _ => new SheetReadResult(
                    Array.Empty<string>(),
                    Array.Empty<IReadOnlyList<string>>(),
                    0,
                    $"Unsupported extension {ext}"),
            };
        }
        catch (OperationCanceledException)
        {
            // Cancellation must not degrade into an ordinary sampler error —
            // callers rely on OCE to reliably abort the whole discovery.
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "TabularShapeSampler failed on {Path}", candidate.AbsolutePath);
            return new FileCandidateSample(candidate, Shape: null, ErrorMessage: ex.GetType().Name);
        }

        if (read.Error is not null)
        {
            return new FileCandidateSample(candidate, Shape: null, ErrorMessage: read.Error);
        }

        int primaryKeyIndex = DetectPrimaryKeyColumn(read.Rows, tabular?.PrimaryKeyPattern);
        bool matchesHints = HeadersMatchHints(read.Headers, tabular?.ColumnHints);

        return new FileCandidateSample(
            Candidate: candidate,
            Shape: new TabularShapeSample(
                ColumnHeaders: read.Headers,
                RowCount: read.RowCount,
                PrimaryKeyColumnIndex: primaryKeyIndex,
                StructureMatchesHints: matchesHints));
    }

    // ---------------------------------------------------------------------
    // Excel (.xlsx)
    // ---------------------------------------------------------------------

    private static SheetReadResult ReadExcel(string path, CancellationToken ct)
    {
        // ClosedXML opens the underlying OPC package read-only here — compatible with Excel.exe
        // holding a normal editing lock on the workbook.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var wb = new XLWorkbook(stream);
        var ws = wb.Worksheets.FirstOrDefault();
        var firstRow = ws?.FirstRowUsed();
        var lastRow = ws?.LastRowUsed();
        var firstCol = ws?.FirstColumnUsed();
        var lastCol = ws?.LastColumnUsed();
        if (ws is null || firstRow is null || lastRow is null || firstCol is null || lastCol is null)
        {
            return new SheetReadResult(
                Array.Empty<string>(), Array.Empty<IReadOnlyList<string>>(), 0, "Empty workbook");
        }

        int startRow = firstRow.RowNumber();
        int endRow = lastRow.RowNumber();
        int startCol = firstCol.ColumnNumber();
        int endCol = lastCol.ColumnNumber();

        var headers = new List<string>(endCol - startCol + 1);
        for (int c = startCol; c <= endCol; c++)
        {
            headers.Add((ws.Cell(startRow, c).GetString() ?? string.Empty).Trim());
        }

        var rows = new List<IReadOnlyList<string>>();
        int rowCount = Math.Max(0, endRow - startRow); // excludes header
        int sampleEnd = Math.Min(endRow, startRow + SampleDepth);
        for (int r = startRow + 1; r <= sampleEnd; r++)
        {
            ct.ThrowIfCancellationRequested();
            var row = new List<string>(headers.Count);
            for (int c = startCol; c <= endCol; c++)
            {
                row.Add((ws.Cell(r, c).GetString() ?? string.Empty).Trim());
            }
            rows.Add(row);
        }

        return new SheetReadResult(headers, rows, rowCount, Error: null);
    }

    // ---------------------------------------------------------------------
    // CSV / TSV — minimal RFC 4180 (quoted fields with commas, escaped quotes,
    // embedded newlines inside quotes).
    // ---------------------------------------------------------------------

    private static SheetReadResult ReadDelimited(string path, char separator, CancellationToken ct)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);

        // Stream-parse so we can handle embedded newlines inside quoted fields.
        var parser = new CsvStreamParser(reader, separator);
        var header = parser.ReadRow(ct);
        if (header is null)
        {
            return new SheetReadResult(
                Array.Empty<string>(), Array.Empty<IReadOnlyList<string>>(), 0, "Empty file");
        }

        var headers = header.Select(h => h.Trim()).ToList();

        var rows = new List<IReadOnlyList<string>>();
        int rowCount = 0;
        IReadOnlyList<string>? row;
        while ((row = parser.ReadRow(ct)) is not null)
        {
            rowCount++;
            if (rows.Count < SampleDepth)
            {
                rows.Add(row.Select(v => v.Trim()).ToList());
            }
        }

        return new SheetReadResult(headers, rows, rowCount, Error: null);
    }

    // ---------------------------------------------------------------------
    // Pattern detection
    // ---------------------------------------------------------------------

    private static int DetectPrimaryKeyColumn(
        IReadOnlyList<IReadOnlyList<string>> sampleRows,
        ExpectedColumnPattern? pattern)
    {
        if (pattern is null || sampleRows.Count == 0) return TabularShapeSample.NoPrimaryKey;

        Regex regex;
        try
        {
            regex = new Regex(pattern.Regex, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        }
        catch (ArgumentException)
        {
            return TabularShapeSample.NoPrimaryKey;
        }

        int columns = sampleRows[0].Count;
        int bestColumn = TabularShapeSample.NoPrimaryKey;
        int bestMatches = 0;

        for (int c = 0; c < columns; c++)
        {
            int matches = 0;
            foreach (var row in sampleRows)
            {
                if (c >= row.Count) continue;
                var cell = row[c];
                if (string.IsNullOrWhiteSpace(cell)) continue;
                try
                {
                    if (regex.IsMatch(cell)) matches++;
                }
                catch (RegexMatchTimeoutException) { /* skip this cell, keep counting */ }
            }
            if (matches >= pattern.MinSampleMatches && matches > bestMatches)
            {
                bestMatches = matches;
                bestColumn = c;
            }
        }

        return bestColumn;
    }

    private static bool HeadersMatchHints(IReadOnlyList<string> headers, IReadOnlyList<string>? hints)
    {
        if (hints is null || hints.Count == 0) return false;
        foreach (var header in headers)
        {
            if (string.IsNullOrWhiteSpace(header)) continue;
            var lowered = header.ToLowerInvariant();
            foreach (var hint in hints)
            {
                if (string.IsNullOrWhiteSpace(hint)) continue;
                if (lowered.Contains(hint.ToLowerInvariant())) return true;
            }
        }
        return false;
    }

    private readonly record struct SheetReadResult(
        IReadOnlyList<string> Headers,
        IReadOnlyList<IReadOnlyList<string>> Rows,
        int RowCount,
        string? Error);

    // ---------------------------------------------------------------------
    // Minimal RFC 4180 stream parser
    // ---------------------------------------------------------------------

    private sealed class CsvStreamParser
    {
        private readonly TextReader _reader;
        private readonly char _separator;
        private readonly StringBuilder _field = new();

        public CsvStreamParser(TextReader reader, char separator)
        {
            _reader = reader;
            _separator = separator;
        }

        public IReadOnlyList<string>? ReadRow(CancellationToken ct)
        {
            if (_reader.Peek() == -1) return null;

            var fields = new List<string>();
            bool inQuotes = false;

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                int ch = _reader.Read();
                if (ch == -1)
                {
                    // EOF flushes the current field + row.
                    fields.Add(_field.ToString());
                    _field.Clear();
                    return fields;
                }

                if (inQuotes)
                {
                    if (ch == '"')
                    {
                        // Escaped double-quote inside a quoted field.
                        if (_reader.Peek() == '"')
                        {
                            _reader.Read();
                            _field.Append('"');
                        }
                        else
                        {
                            inQuotes = false;
                        }
                    }
                    else
                    {
                        _field.Append((char)ch);
                    }
                }
                else
                {
                    if (ch == '"' && _field.Length == 0)
                    {
                        inQuotes = true;
                    }
                    else if (ch == _separator)
                    {
                        fields.Add(_field.ToString());
                        _field.Clear();
                    }
                    else if (ch == '\r')
                    {
                        // Swallow \n if this is a CRLF line ending.
                        if (_reader.Peek() == '\n') _reader.Read();
                        fields.Add(_field.ToString());
                        _field.Clear();
                        return fields;
                    }
                    else if (ch == '\n')
                    {
                        fields.Add(_field.ToString());
                        _field.Clear();
                        return fields;
                    }
                    else
                    {
                        _field.Append((char)ch);
                    }
                }
            }
        }
    }
}
