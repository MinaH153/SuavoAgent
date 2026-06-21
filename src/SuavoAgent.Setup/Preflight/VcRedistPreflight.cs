// src/SuavoAgent.Setup/Preflight/VcRedistPreflight.cs
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SuavoAgent.Setup.Preflight;

/// <summary>Detect → (if missing) download+verify → silent install → re-verify the VC++ x64 runtime.</summary>
public sealed class VcRedistPreflight
{
    // Pinned in Task 6 after uploading vc_redist.x64.exe as a release asset on MinaH153/SuavoAgent.
    public const string AssetUrl = "https://github.com/MinaH153/SuavoAgent/releases/download/vcredist-x64-v1/vc_redist.x64.exe";
    public const string Sha256 = "cc0ff0eb1dc3f5188ae6300faef32bf5beeba4bdd6e8e445a9184072096b713b";

    private readonly VcRedistChecker _checker;
    private readonly Func<VcRedistProvider> _providerFactory;
    private readonly VcRedistInstaller _installer;

    public VcRedistPreflight(
        VcRedistChecker checker, Func<VcRedistProvider> providerFactory, VcRedistInstaller installer)
    {
        _checker = checker;
        _providerFactory = providerFactory;
        _installer = installer;
    }

    public async Task<VcRedistPreflightOutcome> EnsureAsync(string tempDir, CancellationToken ct)
    {
        if (_checker.Check().Installed)
            return new VcRedistPreflightOutcome(VcRedistPreflightState.AlreadyPresent, "VC++ runtime present", false);

        try
        {
            var dest = Path.Combine(tempDir, "vc_redist.x64.exe");
            var path = await _providerFactory().EnsureLocalAsync(dest, ct);
            var result = await _installer.InstallAsync(path, ct);
            try { File.Delete(path); } catch { /* best effort */ }

            return result.Success
                ? new VcRedistPreflightOutcome(VcRedistPreflightState.Installed,
                    "Installed Microsoft Visual C++ 2015-2022 x64 Redistributable", result.RebootPending)
                : new VcRedistPreflightOutcome(VcRedistPreflightState.Failed,
                    $"VC++ install did not verify (exit {result.ExitCode}). Install vc_redist.x64.exe manually, then retry.", false);
        }
        catch (Exception ex)
        {
            return new VcRedistPreflightOutcome(VcRedistPreflightState.Failed,
                $"VC++ runtime install failed: {ex.Message}. Run: winget install Microsoft.VCRedist.2015+.x64", false);
        }
    }
}

public enum VcRedistPreflightState { AlreadyPresent, Installed, Failed }
public sealed record VcRedistPreflightOutcome(VcRedistPreflightState State, string Detail, bool RebootPending);
