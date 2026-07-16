// src/SuavoAgent.Setup/Preflight/VcRedistInstaller.cs
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using SuavoAgent.Diagnostics.Maintenance;

namespace SuavoAgent.Setup.Preflight;

/// <summary>Runs vc_redist.x64.exe silently and re-verifies the runtime is actually present afterward.</summary>
public sealed class VcRedistInstaller
{
    private static readonly int[] SuccessExitCodes = { 0, 3010, 1638 };
    private readonly Func<string, string, CancellationToken, Task<int>> _runProcess;
    private readonly VcRedistChecker _checker;
    private readonly Func<string, bool> _verifyBeforeLaunch;

    public VcRedistInstaller(
        Func<string, string, CancellationToken, Task<int>>? runProcess = null,
        VcRedistChecker? checker = null,
        Func<string, bool>? verifyBeforeLaunch = null)
    {
        _runProcess = runProcess ?? RunProcessAsync;
        _checker = checker ?? new VcRedistChecker();
        _verifyBeforeLaunch = verifyBeforeLaunch ?? (path =>
            PrivilegedExecutableStaging.VerifyMicrosoftExecutable(
                path,
                VcRedistPreflight.Sha256));
    }

    public async Task<VcRedistInstallResult> InstallAsync(string installerPath, CancellationToken ct)
    {
        // Keep the trust check adjacent to process creation. The protected DACL
        // prevents the unelevated same-SID user from replacing the closed file
        // between this check and Process.Start.
        bool trusted;
        try { trusted = _verifyBeforeLaunch(installerPath); }
        catch { trusted = false; }
        if (!trusted)
            return new VcRedistInstallResult(
                Success: false,
                ExitCode: -1,
                RebootPending: false,
                VerifiedAfter: false);

        var exit = await _runProcess(installerPath, "/install /quiet /norestart", ct);
        var codeOk = Array.IndexOf(SuccessExitCodes, exit) >= 0;
        var verified = _checker.Check().Installed;
        return new VcRedistInstallResult(
            Success: codeOk && verified,
            ExitCode: exit,
            RebootPending: exit == 3010,
            VerifiedAfter: verified);
    }

    private static async Task<int> RunProcessAsync(string path, string args, CancellationToken ct)
    {
        using var p = new Process
        {
            StartInfo = new ProcessStartInfo(path, args) { UseShellExecute = false, CreateNoWindow = true }
        };
        p.Start();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(180));
        await p.WaitForExitAsync(timeout.Token);
        return p.ExitCode;
    }
}

public sealed record VcRedistInstallResult(bool Success, int ExitCode, bool RebootPending, bool VerifiedAfter);
