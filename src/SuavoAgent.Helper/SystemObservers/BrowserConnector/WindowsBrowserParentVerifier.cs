using System.Runtime.InteropServices;

namespace SuavoAgent.Helper.SystemObservers.BrowserConnector;

/// <summary>
/// Corroborates native-channel authority by verifying that the native host's
/// direct parent is the expected signed browser. A non-zero --parent-window
/// owner is verified independently because Chromium may assign the visible
/// window and native host to different browser processes. PPID is never the
/// authority signal; <see cref="WindowsBrowserNativeChannelVerifier"/> proves
/// the browser-owned native-messaging stdin/stdout peers first.
/// </summary>
public sealed class WindowsBrowserParentVerifier : IBrowserParentVerifier
{
    public ValueTask<BrowserParentVerification> VerifyAsync(
        BrowserConnectorAuthorityEntry authorization,
        nint parentWindowHandle,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
            return ValueTask.FromResult(BrowserParentVerification.Deny(
                BrowserConnectorReasonCodes.UnsupportedPlatform));

        if (!TryGetParentProcessId((uint)Environment.ProcessId, out var parentProcessId) ||
            !VerifyBrowserProcess(parentProcessId, authorization))
        {
            return ValueTask.FromResult(BrowserParentVerification.Deny(
                BrowserConnectorReasonCodes.ParentBrowserUntrusted));
        }

        if (parentWindowHandle != 0)
        {
            _ = GetWindowThreadProcessId(parentWindowHandle, out var windowProcessId);
            if (windowProcessId == 0 || !VerifyBrowserProcess(windowProcessId, authorization))
            {
                return ValueTask.FromResult(BrowserParentVerification.Deny(
                    BrowserConnectorReasonCodes.ParentBrowserMismatch));
            }
        }

        return ValueTask.FromResult(BrowserParentVerification.Allow());
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool VerifyBrowserProcess(
        uint processId,
        BrowserConnectorAuthorityEntry authorization) =>
        WindowsBrowserProcessTrustVerifier.Verify(processId, authorization);

    internal static bool IsExpectedPublisherSubject(
        BrowserFamily browser,
        string? subject) =>
        WindowsBrowserProcessTrustVerifier.IsExpectedPublisherSubject(browser, subject);

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool TryGetParentProcessId(uint currentProcessId, out uint parentProcessId)
    {
        parentProcessId = 0;
        var snapshot = CreateToolhelp32Snapshot(0x00000002, 0); // TH32CS_SNAPPROCESS
        if (snapshot == InvalidHandleValue)
            return false;

        try
        {
            var entry = new ProcessEntry32
            {
                Size = (uint)Marshal.SizeOf<ProcessEntry32>(),
            };
            if (!Process32FirstW(snapshot, ref entry))
                return false;

            do
            {
                if (entry.ProcessId != currentProcessId)
                    continue;
                parentProcessId = entry.ParentProcessId;
                return parentProcessId != 0;
            }
            while (Process32NextW(snapshot, ref entry));

            return false;
        }
        finally
        {
            _ = CloseHandle(snapshot);
        }
    }

    private static readonly nint InvalidHandleValue = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public nint DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(nint snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(nint snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint windowHandle, out uint processId);
}
