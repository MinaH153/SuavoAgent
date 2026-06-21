// src/SuavoAgent.Setup/Verify/PipePingProbe.cs
using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace SuavoAgent.Setup.Verify;

/// <summary>Confirms the Core command pipe (SuavoAgent-cmd-{nonce}) is reachable — proof Core is up and serving.</summary>
public sealed class PipePingProbe
{
    private readonly Func<string?> _readNonce;
    private readonly Func<string, CancellationToken, Task<bool>> _tryConnect;

    public PipePingProbe(
        Func<string?>? readNonce = null,
        Func<string, CancellationToken, Task<bool>>? tryConnect = null)
    {
        _readNonce = readNonce ?? ReadNonce;
        _tryConnect = tryConnect ?? TryConnectReal;
    }

    public async Task<GateResult> CheckAsync(CancellationToken ct)
    {
        var nonce = _readNonce()?.Trim();
        if (string.IsNullOrEmpty(nonce))
            return new GateResult("Pipe", GateState.Warn, "Agent pipe not advertised yet");
        var pipeName = $"SuavoAgent-cmd-{nonce}";
        var ok = await _tryConnect(pipeName, ct);
        return ok
            ? new GateResult("Pipe", GateState.Ok, "Core command pipe reachable")
            : new GateResult("Pipe", GateState.Fail, "Core command pipe unreachable");
    }

    private static string? ReadNonce()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SuavoAgent", "pipe.nonce");
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch { return null; }
    }

    private static async Task<bool> TryConnectReal(string pipeName, CancellationToken ct)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
            await pipe.ConnectAsync(TimeSpan.FromSeconds(5), ct);
            return pipe.IsConnected;
        }
        catch { return false; }
    }
}
