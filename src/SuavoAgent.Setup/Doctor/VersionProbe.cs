// src/SuavoAgent.Setup/Doctor/VersionProbe.cs
using System;
using SuavoAgent.Setup.Verify;

namespace SuavoAgent.Setup.Doctor;

/// <summary>Reports the installed agent version (Core.exe file version). Read-only.</summary>
public sealed class VersionProbe
{
    private readonly Func<string?> _readCoreFileVersion;
    public VersionProbe(Func<string?> readCoreFileVersion) => _readCoreFileVersion = readCoreFileVersion;

    public GateResult Check()
    {
        var v = _readCoreFileVersion();
        return string.IsNullOrWhiteSpace(v)
            ? new GateResult("Version", GateState.Warn, "Agent version unknown (Core.exe not found)")
            : new GateResult("Version", GateState.Ok, $"SuavoAgent {v}");
    }
}
