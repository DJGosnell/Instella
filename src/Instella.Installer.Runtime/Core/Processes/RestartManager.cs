using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Instella.Installer.Runtime.Core.Processes;

/// <summary>
/// The Windows Restart Manager (<c>rstrtmgr.dll</c>), used only to ask which processes hold
/// a set of files open or loaded. Closing is done by <see cref="RunningAppGate"/>, not by
/// <c>RmShutdown</c>: that one decides its own timeouts and sends WM_QUERYENDSESSION, which
/// apps treat as a logoff.
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class RestartManager
{
    private const int ErrorMoreData = 234;
    private const int CchRmSessionKey = 32;

    // RM_APP_TYPE
    private const int RmService = 3;
    private const int RmExplorer = 4;
    private const int RmCritical = 1000;

    [StructLayout(LayoutKind.Sequential)]
    private struct RM_UNIQUE_PROCESS
    {
        public uint dwProcessId;
        public uint ProcessStartTimeLow;
        public uint ProcessStartTimeHigh;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RM_PROCESS_INFO
    {
        public RM_UNIQUE_PROCESS Process;
        public fixed char strAppName[256];
        public fixed char strServiceShortName[64];
        public int ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        public int bRestartable;
    }

    [LibraryImport("rstrtmgr.dll")]
    private static partial int RmStartSession(out uint pSessionHandle, uint dwSessionFlags, char* strSessionKey);

    [LibraryImport("rstrtmgr.dll")]
    private static partial int RmEndSession(uint dwSessionHandle);

    [LibraryImport("rstrtmgr.dll")]
    private static partial int RmRegisterResources(uint dwSessionHandle, uint nFiles, nint* rgsFileNames,
        uint nApplications, nint rgApplications, uint nServices, nint rgsServiceNames);

    [LibraryImport("rstrtmgr.dll")]
    private static partial int RmGetList(uint dwSessionHandle, out uint pnProcInfoNeeded, ref uint pnProcInfo,
        RM_PROCESS_INFO* rgAffectedApps, out uint lpdwRebootReasons);

    /// <summary>
    /// Every process that has one of <paramref name="files"/> open or loaded. Throws
    /// <see cref="InvalidOperationException"/> with the Win32 error when Restart Manager fails.
    /// </summary>
    public static List<LockingProcess> FindLockingProcesses(IReadOnlyList<string> files)
    {
        var result = new List<LockingProcess>();
        if (files.Count == 0) return result;

        var key = stackalloc char[CchRmSessionKey + 1];
        Check(RmStartSession(out var session, 0, key), "RmStartSession");
        var names = new nint[files.Count];
        try
        {
            for (var i = 0; i < files.Count; i++) names[i] = Marshal.StringToHGlobalUni(files[i]);
            fixed (nint* p = names)
                Check(RmRegisterResources(session, (uint)names.Length, p, 0, 0, 0, 0), "RmRegisterResources");

            var infos = new RM_PROCESS_INFO[8];
            while (true)
            {
                var count = (uint)infos.Length;
                int rc;
                uint needed;
                fixed (RM_PROCESS_INFO* p = infos)
                    rc = RmGetList(session, out needed, ref count, p, out _);
                if (rc == ErrorMoreData)
                {
                    infos = new RM_PROCESS_INFO[needed + 4];
                    continue;
                }
                Check(rc, "RmGetList");

                for (var i = 0; i < count; i++)
                {
                    ref var info = ref infos[i];
                    string appName;
                    fixed (char* n = info.strAppName) appName = new string(n);
                    var startTime = ((long)info.Process.ProcessStartTimeHigh << 32) | info.Process.ProcessStartTimeLow;
                    var canClose = info.ApplicationType is not (RmService or RmExplorer or RmCritical);
                    result.Add(new LockingProcess((int)info.Process.dwProcessId, appName, canClose, startTime));
                }
                return result;
            }
        }
        finally
        {
            foreach (var n in names)
                if (n != 0) Marshal.FreeHGlobal(n);
            RmEndSession(session);
        }
    }

    private static void Check(int rc, string what)
    {
        if (rc != 0) throw new InvalidOperationException($"{what} failed (error {rc})");
    }
}
