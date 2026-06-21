using System;
using SuavoAgent.Core.Ipc;
using SuavoAgent.Core.Workers;

namespace SuavoAgent.Core.Health;

/// <summary>
/// Production <see cref="IHealthSignals"/> implementation. Reads from the
/// real subsystems. Each probe is wrapped in try/catch by the calculator,
/// so individual signal sources can throw safely — they default to false.
/// </summary>
/// <remarks>
/// <see cref="IpcPipeServer.IsConnected"/> tracks only the EVENT pipe (Helper→Core push channel).
/// QA wave-1 C2: a Helper whose COMMAND-pipe listen path is wedged keeps the event pipe open and
/// heartbeating, so the composite reported <c>status=healthy</c> while every actuation/pricing run
/// failed pre-flight — "says healthy but can't act." Fix: <c>HelperAttached</c> stays the event-pipe
/// flag, but <c>IpcConnected</c> now means "IPC fully healthy" = event pipe up AND the command pipe
/// is not conclusively stranded. The command-strand signal is the actuation prober's
/// <c>ConsecutiveStrandFailures</c> (pipe connected but ping dead) — which deliberately EXCLUDES the
/// benign cases (no interactive session, pipe-unreachable=Broker's job, skipped probes), so a
/// locked/headless/idle box is never false-flagged unhealthy. Null tracker / no probe yet → healthy.
/// </remarks>
public sealed class HealthSignalsProvider : IHealthSignals
{
    private readonly IpcPipeServer _ipcPipeServer;
    private readonly RxDetectionWorker _rxWorker;
    private readonly Func<bool> _schemaCanaryGreenProbe;
    private readonly Func<bool> _commandPipeHealthyProbe;

    public HealthSignalsProvider(
        IpcPipeServer ipcPipeServer,
        RxDetectionWorker rxWorker,
        Func<bool> schemaCanaryGreenProbe,
        Func<bool> commandPipeHealthyProbe)
    {
        _ipcPipeServer = ipcPipeServer;
        _rxWorker = rxWorker;
        _schemaCanaryGreenProbe = schemaCanaryGreenProbe;
        _commandPipeHealthyProbe = commandPipeHealthyProbe;
    }

    public HealthSignalsSnapshot Snapshot() => new(
        // HelperAttached = the EVENT pipe is up. IpcConnected = event pipe up AND the COMMAND pipe is
        // not conclusively stranded (QA C2) — see class <remarks>. The two now legitimately diverge.
        HelperAttached:     _ipcPipeServer.IsConnected,
        IpcConnected:       _ipcPipeServer.IsConnected && _commandPipeHealthyProbe(),
        SchemaCanaryGreen:  _schemaCanaryGreenProbe(),
        // RxDetectionWorker exposes LastDetectionTime (set every detection cycle —
        // see RxDetectionWorker.cs lines 112/201/238/267). No separate
        // LastSuccessfulEmitAt exists; LastDetectionTime is the closest semantic
        // match for "last extraction" used by the 30-minute window in the calculator.
        LastExtractionAt:   _rxWorker.LastDetectionTime);
}
