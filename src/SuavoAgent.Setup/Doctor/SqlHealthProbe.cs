// src/SuavoAgent.Setup/Doctor/SqlHealthProbe.cs
using System;
using System.IO;
using System.Linq;
using SuavoAgent.Setup.Verify;

namespace SuavoAgent.Setup.Doctor;

/// <summary>Classifies the newest core-*.log for the SQL connection outcome. Read-only, fail-soft.</summary>
public sealed class SqlHealthProbe
{
    private readonly Func<string?> _readCoreLog;

    public SqlHealthProbe(Func<string?>? readCoreLog = null)
        => _readCoreLog = readCoreLog ?? ReadNewestCoreLog;

    public GateResult Check()
    {
        var log = _readCoreLog();
        if (string.IsNullOrEmpty(log))
            return new GateResult("SQL", GateState.Warn, "No Core log yet");

        bool Has(string s) => log.Contains(s, StringComparison.OrdinalIgnoreCase);

        // "Error Number:18456" (SQL Server's exact form), not a bare "18456" — the bare digits would
        // false-match a benign log line (timestamp / port / row count). ANONYMOUS LOGON + Login failed
        // already cover the real auth-failure case independently.
        if (Has("ANONYMOUS LOGON") || Has("Login failed") || Has("Error Number:18456"))
            return new GateResult("SQL", GateState.Fail,
                "SQL auth failing — the service account has no SQL login. Use SQL Auth or grant the account DB access.");
        if (Has("certificate chain") || Has("not trusted"))
            return new GateResult("SQL", GateState.Fail,
                "SQL TLS cert not trusted — set Agent.SqlTrustServerCertificate=true (PioneerRx uses a self-signed cert).");
        if (Has("SQL schema fingerprint failed"))
            return new GateResult("SQL", GateState.Fail, "Connected DB is not PioneerRx (schema fingerprint failed).");
        if (Has("SQL connection failed"))
            return new GateResult("SQL", GateState.Fail, "SQL server unreachable.");
        if (Has("SQL connected to"))
            return new GateResult("SQL", GateState.Ok, "SQL connected.");
        return new GateResult("SQL", GateState.Warn, "No SQL activity logged (pricing may be UiaFirst — no SQL needed).");
    }

    private static string? ReadNewestCoreLog()
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "SuavoAgent", "logs");
            if (!Directory.Exists(dir)) return null;
            var newest = new DirectoryInfo(dir).GetFiles("core-*.log")
                .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
            if (newest is null) return null;
            using var fs = new FileStream(newest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            return reader.ReadToEnd();
        }
        catch { return null; }
    }
}
