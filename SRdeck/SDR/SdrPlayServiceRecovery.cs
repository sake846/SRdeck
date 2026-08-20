using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace SRdeck.SDR;

internal enum SdrPlayDevicePresence
{
    Unknown,
    Absent,
    Present
}

internal enum SdrPlayApiServiceState
{
    Unknown,
    Missing,
    Stopped,
    StartPending,
    StopPending,
    Running,
    Other
}

internal interface ISdrPlayServiceRecoveryPlatform
{
    SdrPlayDevicePresence GetConnectedDevicePresence(out string detail);
    SdrPlayApiServiceState GetServiceState(out string detail);
    bool TryEnsureServiceRunning(TimeSpan timeout, out string detail);
}

internal static class SdrPlayServiceRecovery
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(5);

    public static bool PrepareForApiOpen()
    {
        if (!OperatingSystem.IsWindows()) return true;

        return PrepareForApiOpen(
            new WindowsSdrPlayServiceRecoveryPlatform(),
            StartTimeout,
            SdrPlayDiagnosticLog.Write);
    }

    internal static bool PrepareForApiOpen(
        ISdrPlayServiceRecoveryPlatform platform,
        TimeSpan timeout,
        Action<string, string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(platform);
        log ??= static (_, _) => { };

        SdrPlayDevicePresence presence;
        string presenceDetail;
        try
        {
            presence = platform.GetConnectedDevicePresence(out presenceDetail);
        }
        catch (Exception exception)
        {
            presence = SdrPlayDevicePresence.Unknown;
            presenceDetail = exception.Message;
        }

        if (presence == SdrPlayDevicePresence.Absent)
        {
            log("service-recovery-skip", $"reason=no-connected-rsp detail={presenceDetail}");
            return false;
        }

        // If Windows device enumeration is unavailable, retain the previous API
        // probing behavior. Crucially, do not touch the service without positive
        // evidence that an SDRplay device is connected.
        if (presence == SdrPlayDevicePresence.Unknown)
        {
            log("service-recovery-skip", $"reason=device-presence-unknown detail={presenceDetail}");
            return true;
        }

        SdrPlayApiServiceState state;
        string stateDetail;
        try
        {
            state = platform.GetServiceState(out stateDetail);
        }
        catch (Exception exception)
        {
            state = SdrPlayApiServiceState.Unknown;
            stateDetail = exception.Message;
        }

        if (state == SdrPlayApiServiceState.Running)
        {
            return true;
        }

        if (state is not (SdrPlayApiServiceState.Stopped or
                          SdrPlayApiServiceState.StartPending or
                          SdrPlayApiServiceState.StopPending))
        {
            // Older SDRplay API installations may not expose the v3 service.
            // Keep API probing compatible with those installations.
            log("service-recovery-skip", $"reason=service-state-{state} detail={stateDetail}");
            return true;
        }

        bool started;
        string startDetail;
        try
        {
            started = platform.TryEnsureServiceRunning(timeout, out startDetail);
        }
        catch (Exception exception)
        {
            started = false;
            startDetail = exception.Message;
        }

        log(
            "service-recovery",
            $"initialState={state} result={(started ? "running" : "failed")} detail={startDetail}");
        return started;
    }
}

internal sealed class WindowsSdrPlayServiceRecoveryPlatform : ISdrPlayServiceRecoveryPlatform
{
    private const string ServiceName = "SDRplayAPIService";
    private const string SdrPlayUsbVendorPrefix = "USB\\VID_1DF7&";

    private const uint DigcfPresent = 0x00000002;
    private const uint DigcfAllClasses = 0x00000004;
    private const int ErrorNoMoreItems = 259;
    private const int ErrorServiceDoesNotExist = 1060;
    private const int ErrorServiceAlreadyRunning = 1056;

    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceStart = 0x0010;
    private const int ScStatusProcessInfo = 0;

    private const uint ServiceStopped = 0x00000001;
    private const uint ServiceStartPending = 0x00000002;
    private const uint ServiceStopPending = 0x00000003;
    private const uint ServiceRunning = 0x00000004;

    public SdrPlayDevicePresence GetConnectedDevicePresence(out string detail)
    {
        if (!OperatingSystem.IsWindows())
        {
            detail = "platform-not-windows";
            return SdrPlayDevicePresence.Unknown;
        }

        using SafeDeviceInfoSetHandle deviceInfoSet = SetupDiGetClassDevsW(
            IntPtr.Zero,
            "USB",
            IntPtr.Zero,
            DigcfPresent | DigcfAllClasses);
        if (deviceInfoSet.IsInvalid)
        {
            detail = new Win32Exception(Marshal.GetLastWin32Error()).Message;
            return SdrPlayDevicePresence.Unknown;
        }

        for (uint index = 0; ; index++)
        {
            var deviceInfo = new SpDevinfoData
            {
                Size = (uint)Marshal.SizeOf<SpDevinfoData>()
            };

            if (!SetupDiEnumDeviceInfo(deviceInfoSet, index, ref deviceInfo))
            {
                int error = Marshal.GetLastWin32Error();
                if (error == ErrorNoMoreItems)
                {
                    detail = "present-usb-device-not-found";
                    return SdrPlayDevicePresence.Absent;
                }

                detail = new Win32Exception(error).Message;
                return SdrPlayDevicePresence.Unknown;
            }

            var instanceId = new StringBuilder(512);
            if (!SetupDiGetDeviceInstanceIdW(
                    deviceInfoSet,
                    ref deviceInfo,
                    instanceId,
                    instanceId.Capacity,
                    out _))
            {
                continue;
            }

            if (instanceId.ToString().StartsWith(SdrPlayUsbVendorPrefix, StringComparison.OrdinalIgnoreCase))
            {
                detail = "connected-sdrplay-usb-device";
                return SdrPlayDevicePresence.Present;
            }
        }
    }

    public SdrPlayApiServiceState GetServiceState(out string detail)
    {
        using SafeServiceHandle? service = OpenService(ServiceQueryStatus, out detail, out int errorCode);
        if (service == null)
        {
            return errorCode == ErrorServiceDoesNotExist
                ? SdrPlayApiServiceState.Missing
                : SdrPlayApiServiceState.Unknown;
        }

        if (!TryQueryStatus(service, out ServiceStatusProcess status, out detail))
        {
            return SdrPlayApiServiceState.Unknown;
        }

        return MapState(status.CurrentState);
    }

    public bool TryEnsureServiceRunning(TimeSpan timeout, out string detail)
    {
        using SafeServiceHandle? service = OpenService(
            ServiceQueryStatus | ServiceStart,
            out detail,
            out _);
        if (service == null) return false;

        var stopwatch = Stopwatch.StartNew();
        bool startRequested = false;

        while (stopwatch.Elapsed < timeout)
        {
            if (!TryQueryStatus(service, out ServiceStatusProcess status, out detail))
            {
                return false;
            }

            switch (status.CurrentState)
            {
                case ServiceRunning:
                    detail = startRequested ? "service-started" : "service-already-running";
                    return true;

                case ServiceStopped:
                    if (startRequested)
                    {
                        detail = $"service-stopped-after-start win32ExitCode={status.Win32ExitCode}";
                        return false;
                    }

                    if (!StartServiceW(service, 0, IntPtr.Zero))
                    {
                        int error = Marshal.GetLastWin32Error();
                        if (error != ErrorServiceAlreadyRunning)
                        {
                            detail = new Win32Exception(error).Message;
                            return false;
                        }
                    }

                    startRequested = true;
                    break;

                case ServiceStartPending:
                    startRequested = true;
                    break;

                case ServiceStopPending:
                    break;

                default:
                    detail = $"unsupported-service-state={status.CurrentState}";
                    return false;
            }

            int delayMs = (int)Math.Clamp(status.WaitHint / 10u, 50u, 250u);
            Thread.Sleep(delayMs);
        }

        detail = $"service-start-timeout-ms={timeout.TotalMilliseconds:F0}";
        return false;
    }

    private static SafeServiceHandle? OpenService(
        uint desiredAccess,
        out string detail,
        out int errorCode)
    {
        using SafeServiceHandle serviceManager = OpenSCManagerW(null, null, ScManagerConnect);
        if (serviceManager.IsInvalid)
        {
            errorCode = Marshal.GetLastWin32Error();
            detail = new Win32Exception(errorCode).Message;
            return null;
        }

        SafeServiceHandle service = OpenServiceW(serviceManager, ServiceName, desiredAccess);
        if (service.IsInvalid)
        {
            errorCode = Marshal.GetLastWin32Error();
            detail = new Win32Exception(errorCode).Message;
            service.Dispose();
            return null;
        }

        errorCode = 0;
        detail = "service-opened";
        return service;
    }

    private static bool TryQueryStatus(
        SafeServiceHandle service,
        out ServiceStatusProcess status,
        out string detail)
    {
        int size = Marshal.SizeOf<ServiceStatusProcess>();
        if (!QueryServiceStatusEx(service, ScStatusProcessInfo, out status, size, out _))
        {
            detail = new Win32Exception(Marshal.GetLastWin32Error()).Message;
            return false;
        }

        detail = $"state={status.CurrentState} win32ExitCode={status.Win32ExitCode}";
        return true;
    }

    private static SdrPlayApiServiceState MapState(uint state) => state switch
    {
        ServiceStopped => SdrPlayApiServiceState.Stopped,
        ServiceStartPending => SdrPlayApiServiceState.StartPending,
        ServiceStopPending => SdrPlayApiServiceState.StopPending,
        ServiceRunning => SdrPlayApiServiceState.Running,
        _ => SdrPlayApiServiceState.Other
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevinfoData
    {
        public uint Size;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
        public uint ProcessId;
        public uint ServiceFlags;
    }

    private sealed class SafeDeviceInfoSetHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeDeviceInfoSetHandle() : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle() => SetupDiDestroyDeviceInfoList(handle);
    }

    private sealed class SafeServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeServiceHandle() : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeDeviceInfoSetHandle SetupDiGetClassDevsW(
        IntPtr classGuid,
        string? enumerator,
        IntPtr hwndParent,
        uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(
        SafeDeviceInfoSetHandle deviceInfoSet,
        uint memberIndex,
        ref SpDevinfoData deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInstanceIdW(
        SafeDeviceInfoSetHandle deviceInfoSet,
        ref SpDevinfoData deviceInfoData,
        StringBuilder deviceInstanceId,
        int deviceInstanceIdSize,
        out int requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeServiceHandle OpenSCManagerW(
        string? machineName,
        string? databaseName,
        uint desiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeServiceHandle OpenServiceW(
        SafeServiceHandle serviceManager,
        string serviceName,
        uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(
        SafeServiceHandle service,
        int infoLevel,
        out ServiceStatusProcess serviceStatus,
        int bufferSize,
        out int bytesNeeded);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartServiceW(
        SafeServiceHandle service,
        int argumentCount,
        IntPtr argumentVectors);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr serviceHandle);
}
