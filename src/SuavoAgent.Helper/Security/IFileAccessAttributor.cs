namespace SuavoAgent.Helper.Security;

/// <summary>Who touched a watched file (best-effort — may be unknown).</summary>
public readonly record struct FileToucher(string? ProcessName, string? ExePath);

/// <summary>Resolves the process that accessed a watched path. Implementations are best-effort.</summary>
public interface IFileAccessAttributor
{
    FileToucher Attribute(string path);
}

/// <summary>
/// v1 default. Windows <c>FileSystemWatcher</c> does NOT expose the accessing process, and reliable PID
/// resolution (ETW kernel-file-IO / handle enumeration) is a deliberate follow-up. Returning "unknown"
/// is SAFE: the corroborator maps an unknown toucher to reversible DEGRADE (never apoptosis on the first
/// touch, never bricks), and a repeat still escalates — so a real attacker is never observed-away.
/// </summary>
public sealed class NullFileAccessAttributor : IFileAccessAttributor
{
    public FileToucher Attribute(string path) => new(null, null);
}
