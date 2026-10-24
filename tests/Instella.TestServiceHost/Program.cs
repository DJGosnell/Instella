using System.Runtime.InteropServices;

namespace Instella.TestServiceHost;

/// <summary>
/// The smallest own-process service: StartServiceCtrlDispatcher, report Running, wait for
/// SERVICE_CONTROL_STOP, report Stopped. Run outside the SCM it exits 1.
/// </summary>
internal static unsafe partial class Program
{
    private const int SERVICE_WIN32_OWN_PROCESS = 0x10;
    private const int SERVICE_STOPPED = 1;
    private const int SERVICE_START_PENDING = 2;
    private const int SERVICE_STOP_PENDING = 3;
    private const int SERVICE_RUNNING = 4;
    private const int SERVICE_ACCEPT_STOP = 0x1;
    private const int SERVICE_CONTROL_STOP = 0x1;
    private const int SERVICE_CONTROL_INTERROGATE = 0x4;
    private const int NO_ERROR = 0;
    private const int ERROR_CALL_NOT_IMPLEMENTED = 120;

    private static readonly ManualResetEventSlim Stop = new(false);
    private static nint _statusHandle;

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_TABLE_ENTRYW
    {
        public char* lpServiceName;
        public delegate* unmanaged<int, char**, void> lpServiceProc;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_STATUS
    {
        public int dwServiceType;
        public int dwCurrentState;
        public int dwControlsAccepted;
        public int dwWin32ExitCode;
        public int dwServiceSpecificExitCode;
        public int dwCheckPoint;
        public int dwWaitHint;
    }

    private static int Main()
    {
        var name = (char*)Marshal.StringToHGlobalUni(string.Empty);
        try
        {
            var table = stackalloc SERVICE_TABLE_ENTRYW[2];
            table[0].lpServiceName = name;
            table[0].lpServiceProc = &ServiceMain;
            table[1] = default;
            return StartServiceCtrlDispatcherW(table) ? 0 : 1;
        }
        finally
        {
            Marshal.FreeHGlobal((nint)name);
        }
    }

    [UnmanagedCallersOnly]
    private static void ServiceMain(int argc, char** argv)
    {
        var serviceName = argc > 0 ? new string(argv[0]) : string.Empty;
        _statusHandle = RegisterServiceCtrlHandlerExW(serviceName, &Handler, 0);
        if (_statusHandle == 0) return;

        Report(SERVICE_START_PENDING, 0);
        Report(SERVICE_RUNNING, SERVICE_ACCEPT_STOP);
        Stop.Wait();
        Report(SERVICE_STOPPED, 0);
    }

    [UnmanagedCallersOnly]
    private static int Handler(int control, int eventType, nint eventData, nint context)
    {
        switch (control)
        {
            case SERVICE_CONTROL_STOP:
                Report(SERVICE_STOP_PENDING, 0);
                Stop.Set();
                return NO_ERROR;
            case SERVICE_CONTROL_INTERROGATE:
                return NO_ERROR;
            default:
                return ERROR_CALL_NOT_IMPLEMENTED;
        }
    }

    private static void Report(int state, int accepted)
    {
        var status = new SERVICE_STATUS
        {
            dwServiceType = SERVICE_WIN32_OWN_PROCESS,
            dwCurrentState = state,
            dwControlsAccepted = accepted,
            dwWaitHint = state is SERVICE_START_PENDING or SERVICE_STOP_PENDING ? 3000 : 0,
        };
        SetServiceStatus(_statusHandle, &status);
    }

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool StartServiceCtrlDispatcherW(SERVICE_TABLE_ENTRYW* table);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint RegisterServiceCtrlHandlerExW(
        string serviceName, delegate* unmanaged<int, int, nint, nint, int> handler, nint context);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetServiceStatus(nint handle, SERVICE_STATUS* status);
}
