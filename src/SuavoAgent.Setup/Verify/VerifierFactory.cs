using System;
using System.Threading;
using System.Threading.Tasks;

namespace SuavoAgent.Setup.Verify;

/// <summary>
/// Single source of the production self-verify gate set so the GUI install flow and the headless
/// console installer verify identically (Services → Pipe → Brain → Cloud auth). A Fail on any gate
/// blocks "installation complete".
/// </summary>
public static class VerifierFactory
{
    public static PostInstallVerifier BuildDefault() => new(new Func<CancellationToken, Task<GateResult>>[]
    {
        _ => Task.FromResult(ServiceInstaller.ServicesRunningGate()),
        ct => new PipePingProbe().CheckAsync(ct),
        _ => Task.FromResult(new BrainHealthProbe().Check()),
        _ => Task.FromResult(new CloudAuthHealthProbe().Check()),
    });
}
