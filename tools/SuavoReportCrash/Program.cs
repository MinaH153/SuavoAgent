using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SuavoAgent.Tools.ReportCrash;

/// suavo-report-crash.exe entry point. Native AOT, stdlib-only.
/// Invoked by publish.ps1 when a `dotnet publish` step returns non-zero
/// exit code. Writes a JSONL marker to %ProgramData%\SuavoAgent\diagnostics\
/// publish-crash.jsonl with the publish context.
///
/// Per Codex §8.7 RESOLVED v1.0:
/// - NO Sentry SDK reference (transport is publish.ps1's responsibility
///   via the broader Wire path on the next agent run).
/// - NO SuavoAgent.Diagnostics reference (would drag JsonSchema.Net +
///   Sentry SDK + .NET runtime services into a tiny crash reporter,
///   recreating the recursion class this tool exists to prevent).
/// - Hand-rolled schema validation (required args + value bounds).
/// - System.Text.Json source-gen-style write via Utf8JsonWriter.
internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitBadArgs = 2;
    private const int ExitWriteFailed = 3;

    public static int Main(string[] args)
    {
        try
        {
            var parsed = ParseArgs(args);
            if (parsed is null) return ExitBadArgs;

            return WriteCrashRecord(parsed) ? ExitOk : ExitWriteFailed;
        }
        catch (Exception ex)
        {
            // Last-resort marker. publish.ps1 falls back to its own
            // %TEMP%\suavo-crash-report-failed.<pid>.txt write path.
            try
            {
                var tempMarker = Path.Combine(
                    Path.GetTempPath(),
                    $"suavo-crash-report-failed.{Environment.ProcessId}.txt");
                File.WriteAllText(tempMarker,
                    $"suavo-report-crash internal failure: {ex.GetType().FullName}: {ex.Message}");
            }
            catch { /* nothing we can do */ }
            return ExitWriteFailed;
        }
    }

    private static CrashRecord? ParseArgs(string[] args)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal)) continue;
            var key = arg.Substring(2);
            var value = (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                ? args[++i]
                : "";
            // Cap each value at 4KB to bound the JSONL line size.
            if (value.Length > 4096) value = value.Substring(0, 4096);
            dict[key] = value;
        }

        // Required: component + exit-code
        if (!dict.TryGetValue("component", out var component) || string.IsNullOrWhiteSpace(component))
        {
            Console.Error.WriteLine("ERROR: --component is required");
            return null;
        }
        if (!dict.TryGetValue("exit-code", out var exitCodeStr) ||
            !int.TryParse(exitCodeStr, out var exitCode))
        {
            Console.Error.WriteLine("ERROR: --exit-code is required and must be an integer");
            return null;
        }

        return new CrashRecord
        {
            Component = component,
            ExitCode = exitCode,
            Project = dict.GetValueOrDefault("project"),
            StdoutTail = dict.GetValueOrDefault("stdout-tail"),
            BuildSha = dict.GetValueOrDefault("build-sha"),
            PublishPid = ParseIntOrNull(dict.GetValueOrDefault("publish-pid")),
            ParentPsVersion = dict.GetValueOrDefault("parent-ps-version"),
            YubikeyPresent = ParseBoolOrNull(dict.GetValueOrDefault("yubikey")),
            SmartCardServiceRunning = ParseBoolOrNull(dict.GetValueOrDefault("smartcard")),
            // Codex §8.7: hash the EV cert thumbprint, never store raw. The
            // caller passes the raw thumbprint here; we hash it for storage.
            EvCertThumbprintHash = HashIfPresent(dict.GetValueOrDefault("ev-cert-thumbprint")),
            // Pre-hashed input (preferred — caller already hashed)
            EvCertThumbprintHashInput = dict.GetValueOrDefault("ev-cert-thumbprint-hash"),
        };
    }

    private static int? ParseIntOrNull(string? s)
        => int.TryParse(s, out var v) ? v : (int?)null;

    private static bool? ParseBoolOrNull(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (bool.TryParse(s, out var b)) return b;
        if (s.Equals("1", StringComparison.Ordinal) ||
            s.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("present", StringComparison.OrdinalIgnoreCase)) return true;
        if (s.Equals("0", StringComparison.Ordinal) ||
            s.Equals("no", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("absent", StringComparison.OrdinalIgnoreCase)) return false;
        return null;
    }

    private static string? HashIfPresent(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        // Normalize to upper-hex with no separators, then SHA-256 the bytes.
        var normalized = new string(raw.Where(c => Uri.IsHexDigit(c)).ToArray()).ToUpperInvariant();
        if (normalized.Length == 0) return null;
        var bytes = Encoding.ASCII.GetBytes(normalized);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool WriteCrashRecord(CrashRecord record)
    {
        var path = ResolveCrashLogPath();
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using var writer = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = false });

            writer.WriteStartObject();
            writer.WriteString("ts", DateTimeOffset.UtcNow.ToString("o"));
            writer.WriteString("source", "suavo-report-crash");
            writer.WriteString("component", record.Component);
            writer.WriteNumber("exit_code", record.ExitCode);
            writer.WriteString("exit_code_hex", $"0x{(uint)record.ExitCode:X8}");
            WriteStringIfPresent(writer, "project", record.Project);
            WriteStringIfPresent(writer, "stdout_tail", record.StdoutTail);
            WriteStringIfPresent(writer, "build_sha", record.BuildSha);
            if (record.PublishPid.HasValue) writer.WriteNumber("publish_pid", record.PublishPid.Value);
            WriteStringIfPresent(writer, "parent_ps_version", record.ParentPsVersion);
            if (record.YubikeyPresent.HasValue) writer.WriteBoolean("yubikey_present", record.YubikeyPresent.Value);
            if (record.SmartCardServiceRunning.HasValue) writer.WriteBoolean("smartcard_running", record.SmartCardServiceRunning.Value);
            WriteStringIfPresent(writer, "ev_cert_thumbprint_hash", record.EvCertThumbprintHash ?? record.EvCertThumbprintHashInput);
            writer.WriteEndObject();
            writer.Flush();
            // Terminate the JSONL line.
            fs.Write(Encoding.UTF8.GetBytes(Environment.NewLine));
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: failed to write crash record to {path}: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static void WriteStringIfPresent(Utf8JsonWriter writer, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) writer.WriteString(name, value);
    }

    private static string ResolveCrashLogPath()
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        // On non-Windows hosts SpecialFolder maps to /usr/share which
        // exists but is typically NOT writable by an unprivileged user.
        // We test write access by attempting to create the target subdir
        // and fall back to TEMP only when that fails.
        if (string.IsNullOrWhiteSpace(programData) || !TryEnsureWritable(programData))
        {
            programData = Path.GetTempPath();
        }
        return Path.Combine(programData, "SuavoAgent", "diagnostics", "publish-crash.jsonl");
    }

    private static bool TryEnsureWritable(string baseDir)
    {
        try
        {
            var probeDir = Path.Combine(baseDir, "SuavoAgent", "diagnostics");
            Directory.CreateDirectory(probeDir);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Crash-record schema. Mirrors fp-v1 signal contract for the
    /// "Publish" component (spec §3 worked example).
    /// </summary>
    private sealed class CrashRecord
    {
        public required string Component { get; init; }
        public required int ExitCode { get; init; }
        public string? Project { get; init; }
        public string? StdoutTail { get; init; }
        public string? BuildSha { get; init; }
        public int? PublishPid { get; init; }
        public string? ParentPsVersion { get; init; }
        public bool? YubikeyPresent { get; init; }
        public bool? SmartCardServiceRunning { get; init; }
        public string? EvCertThumbprintHash { get; init; }
        public string? EvCertThumbprintHashInput { get; init; }
    }
}
