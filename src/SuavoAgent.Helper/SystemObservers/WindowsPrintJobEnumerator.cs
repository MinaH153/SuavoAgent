using System.Runtime.InteropServices;

namespace SuavoAgent.Helper.SystemObservers;

internal sealed class PrintJobIdentity
{
    public string PrinterName { get; }
    public uint JobId { get; }

    public PrintJobIdentity(string printerName, uint jobId)
    {
        PrinterName = printerName;
        JobId = jobId;
    }

    // Prevent an accidental structured-log/destructuring call from rendering the
    // raw local printer name in a future failure path.
    public override string ToString() => "[redacted print job identity]";
}

internal enum PrintMonitorSignalKind
{
    Ready,
    JobAdded,
    PrinterFailure,
}

/// <summary>
/// A raw printer name is carried only inside Helper long enough to derive an HMAC.
/// Callers must never log or serialize <see cref="Job"/>.
/// </summary>
internal sealed record PrintMonitorSignal(
    PrintMonitorSignalKind Kind,
    PrintJobIdentity? Job = null,
    string? FailureCode = null)
{
    public static PrintMonitorSignal Ready() => new(PrintMonitorSignalKind.Ready);

    public static PrintMonitorSignal JobAdded(string printerName, uint jobId) =>
        new(PrintMonitorSignalKind.JobAdded, new PrintJobIdentity(printerName, jobId));

    public static PrintMonitorSignal PrinterFailure(string failureCode) =>
        new(PrintMonitorSignalKind.PrinterFailure, FailureCode: failureCode);
}

internal interface IPrintJobNotificationSource
{
    bool IsSupported { get; }

    /// <summary>
    /// Blocks until cancellation or a source-level failure. Implementations invoke the
    /// callback serially. Cancellation must release any native wait without closing a
    /// handle out from under an active wait.
    /// </summary>
    Task ObserveAsync(Action<PrintMonitorSignal> onSignal, CancellationToken cancellationToken);
}

internal sealed class PrintSpoolerException : Exception
{
    public int NativeErrorCode { get; }

    public PrintSpoolerException(int nativeErrorCode)
        : base("Windows print spooler notification failed.")
    {
        NativeErrorCode = nativeErrorCode;
    }
}

internal sealed class PrintNotificationException : Exception
{
    public string FailureCode { get; }

    public PrintNotificationException(string failureCode)
        : base("Windows print notification data was invalid.")
    {
        FailureCode = failureCode;
    }
}

/// <summary>
/// Event-driven local print-server monitor. A server subscription avoids opening a
/// potentially slow or broken remote printer one-by-one, so one queue/driver failure
/// cannot stop observation of the other queues. Only job ID and printer name are read;
/// document name, user, machine, status text, and spool content are never requested.
/// </summary>
internal sealed class WindowsPrintJobNotificationSource : IPrintJobNotificationSource
{
    private const uint PrinterChangeAddJob = 0x00000100;
    private const uint PrinterNotifyOptionsRefresh = 0x00000001;
    private const uint PrinterNotifyInfoDiscarded = 0x00000001;
    private const ushort JobNotifyType = 0x0001;
    private const ushort JobNotifyFieldPrinterName = 0x0000;
    private const uint NotifyVersion = 2;
    private const int MaximumNotificationRecords = 10_000;
    private const int MaximumPrinterNameBytes = 4_096;
    private const uint Infinite = 0xFFFFFFFF;
    private const uint WaitObject0 = 0x00000000;
    private const uint WaitFailed = 0xFFFFFFFF;
    private static readonly nint InvalidHandleValue = new(-1);

    public bool IsSupported => OperatingSystem.IsWindows();

    public Task ObserveAsync(
        Action<PrintMonitorSignal> onSignal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onSignal);
        if (!IsSupported) return Task.CompletedTask;
        if (!cancellationToken.CanBeCanceled)
            throw new ArgumentException(
                "A cancellable token is required for native print monitoring.",
                nameof(cancellationToken));

        // Winspool notification setup can be synchronous. Keep it off the async pool,
        // then wait on both the notification object and cancellation object.
        return Task.Factory.StartNew(
            () => ObserveBlocking(onSignal, cancellationToken),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    private static void ObserveBlocking(
        Action<PrintMonitorSignal> onSignal,
        CancellationToken cancellationToken)
    {
        nint printerHandle = nint.Zero;
        nint changeHandle = InvalidHandleValue;
        try
        {
            // NULL means the local print server. This observes jobs across its queues
            // without opening each (possibly unavailable network) printer.
            if (!OpenPrinter(null, out printerHandle, nint.Zero))
                throw new PrintSpoolerException(Marshal.GetLastWin32Error());

            using (var options = NotifyOptionsAllocation.ForPrinterName())
            {
                changeHandle = FindFirstPrinterChangeNotification(
                    printerHandle,
                    PrinterChangeAddJob,
                    0,
                    options.Pointer);
            }

            if (changeHandle == InvalidHandleValue)
                throw new PrintSpoolerException(Marshal.GetLastWin32Error());

            // Refresh after subscribing to close the startup race: jobs already in the
            // local spooler are delivered before Ready, then new jobs arrive by signal.
            ReadRefresh(changeHandle, onSignal);
            onSignal(PrintMonitorSignal.Ready());

            var cancellationHandle = cancellationToken.WaitHandle.SafeWaitHandle.DangerousGetHandle();
            var waitHandles = new[] { changeHandle, cancellationHandle };
            while (!cancellationToken.IsCancellationRequested)
            {
                var waitResult = WaitForMultipleObjects(
                    checked((uint)waitHandles.Length),
                    waitHandles,
                    waitAll: false,
                    Infinite);

                if (waitResult == WaitObject0 + 1) return;
                if (waitResult == WaitFailed)
                    throw new PrintSpoolerException(Marshal.GetLastWin32Error());
                if (waitResult != WaitObject0)
                    throw new PrintNotificationException("notification_wait_invalid");

                var discarded = ReadNext(changeHandle, nint.Zero, onSignal);
                if (!discarded) continue;

                // Microsoft requires an explicit refresh after discarded notification
                // data. Surface the loss and refresh before resuming; never silently
                // claim a lossless stream.
                onSignal(PrintMonitorSignal.PrinterFailure("notification_overflow"));
                ReadRefresh(changeHandle, onSignal);
            }
        }
        finally
        {
            // The printer handle must outlive the notification handle.
            if (changeHandle != InvalidHandleValue)
                _ = FindClosePrinterChangeNotification(changeHandle);
            if (printerHandle != nint.Zero)
                _ = ClosePrinter(printerHandle);
        }
    }

    private static void ReadRefresh(
        nint changeHandle,
        Action<PrintMonitorSignal> onSignal)
    {
        using var refresh = NotifyOptionsAllocation.ForRefresh();
        if (ReadNext(changeHandle, refresh.Pointer, onSignal))
            throw new PrintNotificationException("notification_refresh_incomplete");
    }

    private static bool ReadNext(
        nint changeHandle,
        nint options,
        Action<PrintMonitorSignal> onSignal)
    {
        if (!FindNextPrinterChangeNotification(
                changeHandle,
                out var change,
                options,
                out var notificationInfo))
        {
            throw new PrintSpoolerException(Marshal.GetLastWin32Error());
        }

        if (notificationInfo == nint.Zero)
        {
            if ((change & PrinterChangeAddJob) != 0)
                onSignal(PrintMonitorSignal.PrinterFailure("notification_identity_missing"));
            return false;
        }
        try
        {
            var flags = unchecked((uint)Marshal.ReadInt32(notificationInfo, sizeof(uint)));
            var count = unchecked((uint)Marshal.ReadInt32(notificationInfo, sizeof(uint) * 2));
            if (count > MaximumNotificationRecords)
                throw new PrintNotificationException("notification_batch_too_large");

            var dataOffset = Align(sizeof(uint) * 3, IntPtr.Size);
            var unionOffset = Align(sizeof(ushort) * 2 + sizeof(uint) * 2, IntPtr.Size);
            var pointerOffsetWithinUnion = IntPtr.Size == 8 ? sizeof(ulong) : sizeof(uint);
            var dataSize = unionOffset + pointerOffsetWithinUnion + IntPtr.Size;

            var sawExpectedRecord = false;
            for (var index = 0u; index < count; index++)
            {
                var record = nint.Add(
                    notificationInfo,
                    checked(dataOffset + checked((int)index * dataSize)));
                var type = unchecked((ushort)Marshal.ReadInt16(record, 0));
                var field = unchecked((ushort)Marshal.ReadInt16(record, sizeof(ushort)));
                if (type != JobNotifyType || field != JobNotifyFieldPrinterName) continue;
                sawExpectedRecord = true;

                var jobId = unchecked((uint)Marshal.ReadInt32(
                    record,
                    sizeof(ushort) * 2 + sizeof(uint)));
                var byteCount = Marshal.ReadInt32(record, unionOffset);
                var valuePointer = Marshal.ReadIntPtr(
                    record,
                    unionOffset + pointerOffsetWithinUnion);

                var printerName = ReadBoundedPrinterName(valuePointer, byteCount);
                if (jobId == 0 || printerName is null)
                {
                    // One malformed queue record is degraded, but does not prevent
                    // valid records for other queues in the same notification batch.
                    onSignal(PrintMonitorSignal.PrinterFailure("notification_identity_invalid"));
                    continue;
                }

                onSignal(PrintMonitorSignal.JobAdded(printerName, jobId));
            }

            if ((change & PrinterChangeAddJob) != 0 && !sawExpectedRecord)
                onSignal(PrintMonitorSignal.PrinterFailure("notification_identity_missing"));

            return (flags & PrinterNotifyInfoDiscarded) != 0;
        }
        finally
        {
            _ = FreePrinterNotifyInfo(notificationInfo);
        }
    }

    private static string? ReadBoundedPrinterName(nint pointer, int byteCount)
    {
        if (pointer == nint.Zero
            || byteCount <= 0
            || byteCount > MaximumPrinterNameBytes
            || (byteCount & 1) != 0)
        {
            return null;
        }

        var value = Marshal.PtrToStringUni(pointer, byteCount / sizeof(char));
        value = value?.TrimEnd('\0');
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static int Align(int value, int alignment) =>
        checked((value + alignment - 1) & ~(alignment - 1));

    private sealed class NotifyOptionsAllocation : IDisposable
    {
        private readonly nint _types;
        private readonly nint _fields;
        public nint Pointer { get; }

        private NotifyOptionsAllocation(
            PrinterNotifyOptions options,
            nint types,
            nint fields)
        {
            _types = types;
            _fields = fields;
            var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<PrinterNotifyOptions>());
            try
            {
                Marshal.StructureToPtr(options, pointer, fDeleteOld: false);
                Pointer = pointer;
            }
            catch
            {
                Marshal.FreeHGlobal(pointer);
                throw;
            }
        }

        public static NotifyOptionsAllocation ForPrinterName()
        {
            var fields = Marshal.AllocHGlobal(sizeof(ushort));
            var types = nint.Zero;
            try
            {
                Marshal.WriteInt16(fields, unchecked((short)JobNotifyFieldPrinterName));
                var optionsType = new PrinterNotifyOptionsType
                {
                    Type = JobNotifyType,
                    Count = 1,
                    Fields = fields,
                };

                types = Marshal.AllocHGlobal(Marshal.SizeOf<PrinterNotifyOptionsType>());
                Marshal.StructureToPtr(optionsType, types, fDeleteOld: false);
                return new NotifyOptionsAllocation(
                    new PrinterNotifyOptions
                    {
                        Version = NotifyVersion,
                        Count = 1,
                        Types = types,
                    },
                    types,
                    fields);
            }
            catch
            {
                if (types != nint.Zero) Marshal.FreeHGlobal(types);
                Marshal.FreeHGlobal(fields);
                throw;
            }
        }

        public static NotifyOptionsAllocation ForRefresh() =>
            new(
                new PrinterNotifyOptions
                {
                    Version = NotifyVersion,
                    Flags = PrinterNotifyOptionsRefresh,
                },
                nint.Zero,
                nint.Zero);

        public void Dispose()
        {
            Marshal.FreeHGlobal(Pointer);
            if (_types != nint.Zero) Marshal.FreeHGlobal(_types);
            if (_fields != nint.Zero) Marshal.FreeHGlobal(_fields);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PrinterNotifyOptions
    {
        public uint Version;
        public uint Flags;
        public uint Count;
        public nint Types;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PrinterNotifyOptionsType
    {
        public ushort Type;
        public ushort Reserved0;
        public uint Reserved1;
        public uint Reserved2;
        public uint Count;
        public nint Fields;
    }

    [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenPrinter(
        string? printerName,
        out nint printerHandle,
        nint defaults);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern nint FindFirstPrinterChangeNotification(
        nint printerHandle,
        uint filter,
        uint options,
        nint printerNotifyOptions);

    [DllImport("winspool.drv", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindNextPrinterChangeNotification(
        nint changeHandle,
        out uint change,
        nint printerNotifyOptions,
        out nint printerNotifyInfo);

    [DllImport("winspool.drv", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClosePrinterChangeNotification(nint changeHandle);

    [DllImport("winspool.drv", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreePrinterNotifyInfo(nint printerNotifyInfo);

    [DllImport("winspool.drv", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClosePrinter(nint printerHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForMultipleObjects(
        uint count,
        [In] nint[] handles,
        [MarshalAs(UnmanagedType.Bool)] bool waitAll,
        uint milliseconds);
}
