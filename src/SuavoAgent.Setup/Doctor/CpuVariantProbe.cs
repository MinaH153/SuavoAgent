// src/SuavoAgent.Setup/Doctor/CpuVariantProbe.cs
using System;
using SuavoAgent.Setup.Verify;

namespace SuavoAgent.Setup.Doctor;

/// <summary>Compares CPU AVX2 capability to the installed brain native-lib variant (.variant marker).</summary>
public sealed class CpuVariantProbe
{
    private readonly Func<bool> _avx2Supported;
    private readonly Func<string?> _readVariantMarker;

    public CpuVariantProbe(Func<bool> avx2Supported, Func<string?> readVariantMarker)
    {
        _avx2Supported = avx2Supported;
        _readVariantMarker = readVariantMarker;
    }

    public GateResult Check()
    {
        var avx2 = _avx2Supported();
        var variant = (_readVariantMarker() ?? "noavx").Trim().ToLowerInvariant();
        if (avx2 && variant == "noavx")
            return new GateResult("Brain CPU variant", GateState.Warn,
                "CPU supports AVX2 but the slower noavx brain build is installed.");
        return new GateResult("Brain CPU variant", GateState.Ok, $"Brain native libs: {variant}");
    }
}
