using System.Security.Cryptography;
using System.Text;
using Serilog;
using SuavoAgent.Contracts.Behavioral;

namespace SuavoAgent.Helper.SystemObservers;

public sealed class SpreadsheetStructureObserver
{
    private static readonly HashSet<string> SpreadsheetProcesses = new(StringComparer.OrdinalIgnoreCase)
        { "EXCEL", "LibreOffice", "soffice", "wps" };

    private readonly BehavioralEventBuffer _buffer;
    private readonly string _pharmacySalt;
    private readonly ILogger _logger;
    private string? _lastFingerprint;
    public int SnapshotCount { get; private set; }

    public SpreadsheetStructureObserver(BehavioralEventBuffer buffer, string pharmacySalt, ILogger logger)
    { _buffer = buffer; _pharmacySalt = pharmacySalt; _logger = logger; }

    public static bool IsSpreadsheetProcess(string processName) => SpreadsheetProcesses.Contains(processName);

    public void OnSpreadsheetFocused(string windowTitle)
    {
        if (string.IsNullOrEmpty(windowTitle)) return;
        var fileType = ExtractFileType(windowTitle);
        var nameHash = UiaPropertyScrubber.HmacHash(windowTitle, _pharmacySalt);
        var fp = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{nameHash}:{fileType}")))[..16].ToLowerInvariant();
        if (fp == _lastFingerprint) return;
        _lastFingerprint = fp;
        _buffer.Enqueue(BehavioralEvent.Interaction("spreadsheet_open", fp, fileType ?? "unknown", "spreadsheet", null, nameHash));
        SnapshotCount++;
    }

    public static string? ExtractFileType(string title)
    {
        var lower = title.ToLowerInvariant();
        foreach (var ext in new[] { ".xlsx", ".xls", ".csv", ".ods", ".xlsm" })
            if (lower.Contains(ext)) return ext.TrimStart('.');
        return null;
    }
}
