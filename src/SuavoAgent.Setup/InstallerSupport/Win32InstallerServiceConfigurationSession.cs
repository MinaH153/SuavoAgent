using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SuavoAgent.Setup.InstallerSupport;

/// <summary>
/// Narrow SCM boundary for the MSI deferred action. It can query and change
/// configuration only; it cannot create, delete, start, or stop a service.
/// </summary>
internal sealed class Win32InstallerServiceConfigurationSession
    : IInstallerServiceConfigurationSession
{
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryConfig = 0x0001;
    private const uint ServiceChangeConfig = 0x0002;
    private const int ErrorInsufficientBuffer = 122;
    private const uint ServiceConfigDelayedAutoStartInfo = 3;
    private const uint ServiceConfigServiceSidInfo = 5;
    private const uint ServiceSidTypeNone = 0;
    private const uint ServiceSidTypeRestricted = 3;
    private const uint MaximumConfigurationBufferBytes = 4096;

    private readonly SafeServiceHandle _serviceControlManager;

    internal Win32InstallerServiceConfigurationSession()
    {
        _serviceControlManager = OpenSCManager(
            lpMachineName: null,
            lpDatabaseName: null,
            dwDesiredAccess: ScManagerConnect);
        if (_serviceControlManager.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public InstallerServiceConfiguration Read(string serviceName)
    {
        using var service = OpenReviewedService(serviceName);
        var delayedAutoStart = ReadConfigurationValue(
            service,
            ServiceConfigDelayedAutoStartInfo);
        var serviceSidType = ReadConfigurationValue(
            service,
            ServiceConfigServiceSidInfo);

        if (delayedAutoStart is not (0 or 1) ||
            serviceSidType is not
                (ServiceSidTypeNone or
                 MsiServiceHardeningTransaction.ServiceSidTypeUnrestricted or
                 ServiceSidTypeRestricted))
        {
            throw new InvalidOperationException("SCM returned an unsupported service configuration value.");
        }

        return new InstallerServiceConfiguration(
            DelayedAutoStart: delayedAutoStart == 1,
            ServiceSidType: serviceSidType);
    }

    public void Write(
        string serviceName,
        InstallerServiceConfiguration configuration)
    {
        if (configuration.ServiceSidType is not
            (ServiceSidTypeNone or
             MsiServiceHardeningTransaction.ServiceSidTypeUnrestricted or
             ServiceSidTypeRestricted))
        {
            throw new ArgumentOutOfRangeException(nameof(configuration));
        }

        using var service = OpenReviewedService(serviceName);
        WriteConfigurationValue(
            service,
            ServiceConfigDelayedAutoStartInfo,
            configuration.DelayedAutoStart ? 1u : 0u);
        WriteConfigurationValue(
            service,
            ServiceConfigServiceSidInfo,
            configuration.ServiceSidType);
    }

    public void Dispose() => _serviceControlManager.Dispose();

    private SafeServiceHandle OpenReviewedService(string serviceName)
    {
        if (!MsiServiceHardeningTransaction.ServiceNames.Contains(
                serviceName,
                StringComparer.Ordinal))
        {
            throw new ArgumentException("The requested service is outside the MSI-owned cohort.", nameof(serviceName));
        }

        var service = OpenService(
            _serviceControlManager,
            serviceName,
            ServiceQueryConfig | ServiceChangeConfig);
        if (!service.IsInvalid)
            return service;

        var error = Marshal.GetLastWin32Error();
        service.Dispose();
        throw new Win32Exception(error);
    }

    private static uint ReadConfigurationValue(
        SafeServiceHandle service,
        uint informationLevel)
    {
        var unexpectedSuccess = QueryServiceConfig2(
            service,
            informationLevel,
            IntPtr.Zero,
            0,
            out var bytesNeeded);
        var firstError = Marshal.GetLastWin32Error();
        if (unexpectedSuccess ||
            firstError != ErrorInsufficientBuffer ||
            bytesNeeded < sizeof(uint) ||
            bytesNeeded > MaximumConfigurationBufferBytes)
        {
            throw new Win32Exception(firstError);
        }

        var buffer = Marshal.AllocHGlobal(checked((int)bytesNeeded));
        try
        {
            if (!QueryServiceConfig2(
                    service,
                    informationLevel,
                    buffer,
                    bytesNeeded,
                    out _))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return unchecked((uint)Marshal.ReadInt32(buffer));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void WriteConfigurationValue(
        SafeServiceHandle service,
        uint informationLevel,
        uint value)
    {
        var buffer = Marshal.AllocHGlobal(sizeof(uint));
        try
        {
            Marshal.WriteInt32(buffer, unchecked((int)value));
            if (!ChangeServiceConfig2(service, informationLevel, buffer))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private sealed class SafeServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeServiceHandle() : base(ownsHandle: true) { }

        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }

    [DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeServiceHandle OpenSCManager(
        string? lpMachineName,
        string? lpDatabaseName,
        uint dwDesiredAccess);

    [DllImport("advapi32.dll", EntryPoint = "OpenServiceW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeServiceHandle OpenService(
        SafeServiceHandle hSCManager,
        string lpServiceName,
        uint dwDesiredAccess);

    [DllImport("advapi32.dll", EntryPoint = "QueryServiceConfig2W", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceConfig2(
        SafeServiceHandle hService,
        uint dwInfoLevel,
        IntPtr lpBuffer,
        uint cbBufSize,
        out uint pcbBytesNeeded);

    [DllImport("advapi32.dll", EntryPoint = "ChangeServiceConfig2W", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeServiceConfig2(
        SafeServiceHandle hService,
        uint dwInfoLevel,
        IntPtr lpInfo);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr hSCObject);
}
