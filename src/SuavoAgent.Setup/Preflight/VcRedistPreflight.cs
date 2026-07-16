// src/SuavoAgent.Setup/Preflight/VcRedistPreflight.cs
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SuavoAgent.Diagnostics.Maintenance;

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
    private readonly Func<string> _createStagingDirectory;
    private readonly Func<string, string, bool> _protectAndVerifyExecutable;
    private readonly Action<string, string?> _cleanupStagingDirectory;

    public VcRedistPreflight(
        VcRedistChecker checker,
        Func<VcRedistProvider> providerFactory,
        VcRedistInstaller installer,
        Func<string>? createStagingDirectory = null,
        Func<string, string, bool>? protectAndVerifyExecutable = null,
        Action<string, string?>? cleanupStagingDirectory = null)
    {
        _checker = checker;
        _providerFactory = providerFactory;
        _installer = installer;
        _createStagingDirectory = createStagingDirectory ??
                                  (() => PrivilegedExecutableStaging.CreateDirectory());
        _protectAndVerifyExecutable = protectAndVerifyExecutable ??
            PrivilegedExecutableStaging.ProtectAndVerifyMicrosoftExecutable;
        _cleanupStagingDirectory = cleanupStagingDirectory ??
                                   PrivilegedExecutableStaging.TryCleanupDirectory;
    }

    public async Task<VcRedistPreflightOutcome> EnsureAsync(CancellationToken ct)
    {
        if (_checker.Check().Installed)
            return new VcRedistPreflightOutcome(VcRedistPreflightState.AlreadyPresent, "VC++ runtime present", false);

        string? stagingDirectory = null;
        string? path = null;
        try
        {
            stagingDirectory = _createStagingDirectory();
            var dest = PrivilegedExecutableStaging.CreateExecutablePath(
                stagingDirectory,
                PrivilegedExecutableStaging.VcRedistFilePrefix);
            path = await _providerFactory().EnsureLocalAsync(dest, ct);
            if (!_protectAndVerifyExecutable(path, Sha256))
                throw new VcRedistVerificationException(
                    "vc_redist failed final placement trust validation.");
            var result = await _installer.InstallAsync(path, ct);

            return result.Success
                ? new VcRedistPreflightOutcome(VcRedistPreflightState.Installed,
                    "Installed Microsoft Visual C++ 2015-2022 x64 Redistributable", result.RebootPending)
                : new VcRedistPreflightOutcome(VcRedistPreflightState.Failed,
                    $"VC++ install did not verify (exit {result.ExitCode}). Install vc_redist.x64.exe manually, then retry.", false);
        }
        catch (Exception)
        {
            return new VcRedistPreflightOutcome(VcRedistPreflightState.Failed,
                "VC++ runtime setup could not complete. Install vc_redist.x64.exe from Microsoft, then retry. Support code: SETUP-RUNTIME-INSTALL", false);
        }
        finally
        {
            if (stagingDirectory is not null)
                _cleanupStagingDirectory(stagingDirectory, path);
        }
    }
}

public enum VcRedistPreflightState { AlreadyPresent, Installed, Failed }
public sealed record VcRedistPreflightOutcome(VcRedistPreflightState State, string Detail, bool RebootPending);
