using System.Runtime.InteropServices;

namespace SuavoAgent.Helper.SystemObservers.BrowserConnector;

internal enum BrowserNativeStandardChannel
{
    Input,
    Output,
}

internal interface IWindowsBrowserNativeChannelSystem
{
    bool IsSupportedPlatform { get; }

    bool TryGetPipeServerProcessId(
        BrowserNativeStandardChannel channel,
        out uint processId);

    bool IsExpectedBrowserProcess(
        uint processId,
        BrowserConnectorAuthorityEntry authorization);

    bool IsSameUserAndSession(uint processId);
}

/// <summary>
/// Establishes native-messaging launch authority from the browser-owned
/// kernel pipe peers behind this process's real standard handles. Parent PID
/// and parent-window checks are separate corroboration, never substitutes.
/// </summary>
public sealed class WindowsBrowserNativeChannelVerifier : IBrowserNativeChannelVerifier
{
    private readonly IWindowsBrowserNativeChannelSystem _system;

    public WindowsBrowserNativeChannelVerifier()
        : this(new WindowsBrowserNativeChannelSystem())
    {
    }

    internal WindowsBrowserNativeChannelVerifier(
        IWindowsBrowserNativeChannelSystem system)
    {
        _system = system ?? throw new ArgumentNullException(nameof(system));
    }

    public ValueTask<BrowserNativeChannelVerification> VerifyAsync(
        BrowserConnectorAuthorityEntry authorization,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_system.IsSupportedPlatform ||
            !_system.TryGetPipeServerProcessId(
                BrowserNativeStandardChannel.Input,
                out var inputPeer) ||
            !_system.TryGetPipeServerProcessId(
                BrowserNativeStandardChannel.Output,
                out var outputPeer) ||
            inputPeer == 0 ||
            inputPeer != outputPeer ||
            !_system.IsExpectedBrowserProcess(inputPeer, authorization) ||
            !_system.IsSameUserAndSession(inputPeer))
        {
            return ValueTask.FromResult(BrowserNativeChannelVerification.Deny());
        }

        return ValueTask.FromResult(BrowserNativeChannelVerification.Allow());
    }
}

/// <summary>
/// Production system boundary. Standard handle and peer identifiers always
/// come from the current process and the Windows kernel; callers cannot
/// provide either value.
/// </summary>
internal sealed class WindowsBrowserNativeChannelSystem : IWindowsBrowserNativeChannelSystem
{
    public bool IsSupportedPlatform => OperatingSystem.IsWindows();

    public bool TryGetPipeServerProcessId(
        BrowserNativeStandardChannel channel,
        out uint processId)
    {
        processId = 0;
        if (!OperatingSystem.IsWindows())
            return false;

        try
        {
            var standardHandleId = channel switch
            {
                BrowserNativeStandardChannel.Input => StandardInputHandle,
                BrowserNativeStandardChannel.Output => StandardOutputHandle,
                _ => 0,
            };
            if (standardHandleId == 0)
                return false;

            var handle = GetStdHandle(standardHandleId);
            return handle != nint.Zero &&
                   handle != InvalidHandleValue &&
                   GetFileType(handle) == FileTypePipe &&
                   GetNamedPipeServerProcessId(handle, out processId) &&
                   processId != 0;
        }
        catch
        {
            processId = 0;
            return false;
        }
    }

    public bool IsExpectedBrowserProcess(
        uint processId,
        BrowserConnectorAuthorityEntry authorization) =>
        OperatingSystem.IsWindows() &&
        WindowsBrowserProcessTrustVerifier.Verify(processId, authorization);

    public bool IsSameUserAndSession(uint processId) =>
        OperatingSystem.IsWindows() &&
        WindowsProcessIdentityVerifier.MatchesCurrent(processId);

    private const int StandardInputHandle = -10;
    private const int StandardOutputHandle = -11;
    private const uint FileTypePipe = 0x0003;
    private static readonly nint InvalidHandleValue = new(-1);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GetStdHandle(int standardHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetFileType(nint handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(
        nint pipe,
        out uint serverProcessId);
}
