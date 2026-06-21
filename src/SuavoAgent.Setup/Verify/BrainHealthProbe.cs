// src/SuavoAgent.Setup/Verify/BrainHealthProbe.cs
using System;
using System.IO;
using System.Linq;

namespace SuavoAgent.Setup.Verify;

/// <summary>
/// Classifies the on-device brain from the Core log. Fails ONLY on a definitive load-failure marker
/// (the Nadim native-lib brick); "enabled but not yet loaded" is Ok (lazy load), disabled is Skip.
/// </summary>
public sealed class BrainHealthProbe
{
    // Detection depends on the Core logging the brain-load failure WITH its exception:
    // `_logger.LogError(ex, "LLamaLocalInference: model load failed")`. "model load failed" matches the
    // message template; "NativeApi" + "TypeInitializationException" match because Serilog serializes the
    // exception's type + stack into the log file (the real Nadim brick throws TypeInitializationException
    // from LLama.Native.NativeApi's static ctor when vcruntime140_1.dll is absent). "missing required
    // native libs" matches the pre-check warning. If that LogError ever drops the `ex` argument, two of
    // these markers silently stop matching — keep the exception in the log call.
    private static readonly string[] FailureMarkers =
        { "model load failed", "NativeApi", "TypeInitializationException", "missing required native libs" };

    private readonly Func<string?> _readCoreLog;

    public BrainHealthProbe(Func<string?>? readCoreLog = null)
        => _readCoreLog = readCoreLog ?? ReadNewestCoreLog;

    public GateResult Check()
    {
        var log = _readCoreLog();
        if (string.IsNullOrEmpty(log))
            return new GateResult("Brain", GateState.Warn, "Brain status not yet logged");

        if (FailureMarkers.Any(m => log.Contains(m, StringComparison.OrdinalIgnoreCase)))
            return new GateResult("Brain", GateState.Fail,
                "On-device brain failed to load (native library). Ensure the VC++ 2015-2022 x64 Redistributable is installed, then restart the Core service.");
        if (log.Contains("model loaded in", StringComparison.OrdinalIgnoreCase))
            return new GateResult("Brain", GateState.Ok, "Brain loaded");
        if (log.Contains("Tier-2 LocalInference disabled", StringComparison.OrdinalIgnoreCase))
            return new GateResult("Brain", GateState.Skip, "Reasoning disabled by config");
        if (log.Contains("Tier-2 LocalInference ENABLED", StringComparison.OrdinalIgnoreCase))
            return new GateResult("Brain", GateState.Ok, "Brain provisioned; loads on first use");
        return new GateResult("Brain", GateState.Warn, "Brain status inconclusive");
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
