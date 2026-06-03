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
/// Architectural note (2026-05-01): the plan's skeleton referenced an
/// <c>IpcPeerVerifier</c> class with a separate <c>IsConnected</c> probe,
/// but no such class exists in this codebase. The peer-validation logic
/// (process-name + image-path checks) lives inline in
/// <see cref="IpcPipeServer.ListenLoop"/> and gates the
/// <see cref="IpcPipeServer.IsConnected"/> flag — i.e. <c>IsConnected</c>
/// only flips true AFTER peer-validation passes. So today the two signals
/// (HelperAttached, IpcConnected) are sourced from the same property and
/// will always agree. Splitting them is left as a future enhancement once
/// a distinct verifier surface exists; the two-field shape is preserved
/// here so the interface stays stable for Task 3 / Task 4.
/// </remarks>
public sealed class HealthSignalsProvider : IHealthSignals
{
    private readonly IpcPipeServer _ipcPipeServer;
    private readonly RxDetectionWorker _rxWorker;
    private readonly Func<bool> _schemaCanaryGreenProbe;

    public HealthSignalsProvider(
        IpcPipeServer ipcPipeServer,
        RxDetectionWorker rxWorker,
        Func<bool> schemaCanaryGreenProbe)
    {
        _ipcPipeServer = ipcPipeServer;
        _rxWorker = rxWorker;
        _schemaCanaryGreenProbe = schemaCanaryGreenProbe;
    }

    public HealthSignalsSnapshot Snapshot() => new(
        // HelperAttached + IpcConnected both come from IpcPipeServer.IsConnected today.
        // See class-level <remarks> for why — peer-validation gates this single flag.
        HelperAttached:     _ipcPipeServer.IsConnected,
        IpcConnected:       _ipcPipeServer.IsConnected,
        SchemaCanaryGreen:  _schemaCanaryGreenProbe(),
        // RxDetectionWorker exposes LastDetectionTime (set every detection cycle —
        // see RxDetectionWorker.cs lines 112/201/238/267). No separate
        // LastSuccessfulEmitAt exists; LastDetectionTime is the closest semantic
        // match for "last extraction" used by the 30-minute window in the calculator.
        LastExtractionAt:   _rxWorker.LastDetectionTime);
}
