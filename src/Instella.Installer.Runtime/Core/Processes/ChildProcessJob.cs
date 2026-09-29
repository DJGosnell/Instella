using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Instella.Installer.Runtime.Core.Processes;

/// <summary>
/// A Windows job object with <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>: every process in it is
/// ended when the handle closes, including when this process dies. Used for the app's upgrade
/// program, so an installer that crashes (and whose files recovery then rolls back) never leaves
/// the program running on.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class ChildProcessJob : IDisposable
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint KillOnJobClose = 0x2000;

    private IntPtr _handle;

    private ChildProcessJob(IntPtr handle) => _handle = handle;

    /// <summary>
    /// Puts <paramref name="process"/> into a new kill-on-close job. Returns null, with the reason in
    /// <paramref name="problem"/>, when that is not possible (the program then still runs).
    /// </summary>
    public static ChildProcessJob? TryAssign(Process process, out string? problem)
    {
        problem = null;
        var handle = CreateJobObjectW(IntPtr.Zero, IntPtr.Zero);
        if (handle == IntPtr.Zero)
        {
            problem = $"CreateJobObject failed: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}";
            return null;
        }

        var job = new ChildProcessJob(handle);
        var info = new ExtendedLimitInformation { BasicLimitInformation = new BasicLimitInformation { LimitFlags = KillOnJobClose } };
        if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformation, ref info, (uint)Marshal.SizeOf<ExtendedLimitInformation>()))
        {
            problem = $"SetInformationJobObject failed: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}";
            job.Dispose();
            return null;
        }

        try
        {
            if (!AssignProcessToJobObject(handle, process.Handle))
            {
                problem = $"AssignProcessToJobObject failed: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}";
                job.Dispose();
                return null;
            }
        }
        catch (InvalidOperationException ex)
        {
            problem = ex.Message;          // the process already exited
            job.Dispose();
            return null;
        }
        return job;
    }

    /// <summary>Closes the job, ending whatever is still in it.</summary>
    public void Dispose()
    {
        if (_handle == IntPtr.Zero) return;
        CloseHandle(_handle);
        _handle = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr CreateJobObjectW(IntPtr jobAttributes, IntPtr name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(IntPtr job, int infoClass, ref ExtendedLimitInformation info, uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);
}
