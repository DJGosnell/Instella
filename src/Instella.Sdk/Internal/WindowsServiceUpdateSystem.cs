using System.ComponentModel;
using System.Runtime.InteropServices;
using Instella.Core.Installation;
using Instella.Core.Platform;
using Instella.Core.Platform.Windows;

namespace Instella.Sdk.Internal;

/// <summary>
/// <see cref="IServiceUpdateSystem"/> over the real OS: the Service Control Manager through
/// <see cref="IServiceManager"/>, the job object through kernel32, and <c>CreateProcessW</c> for
/// a start that outlives the service. Off Windows every check refuses.
/// </summary>
internal sealed partial class WindowsServiceUpdateSystem : IServiceUpdateSystem
{
    private const string NotWindows = "Windows services exist only on Windows";
    private readonly Lazy<IServiceManager> _services = new(() => ServiceManagerFactory.Create(), LazyThreadSafetyMode.ExecutionAndPublication);

    public string? UnsupportedReason => OperatingSystem.IsWindows() ? null : NotWindows;

    public int CurrentProcessId => Environment.ProcessId;

    public bool IsElevated => OperatingSystem.IsWindows() && Environment.IsPrivilegedProcess;

    public string? CheckCanWrite(string directory)
    {
        if (!OperatingSystem.IsWindows()) return NotWindows;
        var state = Path.Combine(directory, InstellaOwnedPaths.StateDirectory);
        foreach (var dir in Directory.Exists(state) ? new[] { directory, state } : [directory])
        {
            var probe = Path.Combine(dir, $".instella-write-check-{Guid.NewGuid():N}.tmp");
            try
            {
                using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return dir == directory ? ex.Message : $"{dir}: {ex.Message}";
            }
            finally
            {
                // DeleteOnClose already removed it; this covers a handle that was never opened.
                try { File.Delete(probe); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        return null;
    }

    public async Task<string?> CheckCanStopAndStartAsync(string serviceName, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return NotWindows;
        var result = await _services.Value.CheckControlAccessAsync(serviceName, ct).ConfigureAwait(false);
        return result.Outcome.Success ? null : result.Outcome.Error ?? "access denied";
    }

    public async Task<int?> GetServiceProcessIdAsync(string serviceName, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var result = await _services.Value.QueryStatusAsync(serviceName, ct).ConfigureAwait(false);
        return result.Outcome.Success && result.Value is { ProcessId: > 0 } status ? status.ProcessId : null;
    }

    public unsafe JobBreakaway GetJobBreakaway()
    {
        if (!OperatingSystem.IsWindows()) return JobBreakaway.Forbidden;
        if (IsProcessInJob(GetCurrentProcess(), IntPtr.Zero, out var inJob) == 0) return JobBreakaway.Forbidden;
        if (inJob == 0) return JobBreakaway.NotInJob;

        // A null job handle queries the job this process belongs to.
        JOBOBJECT_EXTENDED_LIMIT_INFORMATION info;
        if (QueryInformationJobObject(IntPtr.Zero, JobObjectExtendedLimitInformation, &info,
                (uint)sizeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION), IntPtr.Zero) == 0)
            return JobBreakaway.Forbidden;
        return (info.BasicLimitInformation.LimitFlags & (JOB_OBJECT_LIMIT_BREAKAWAY_OK | JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK)) != 0
            ? JobBreakaway.Allowed
            : JobBreakaway.Forbidden;
    }

    public unsafe int StartDetached(string executable, IReadOnlyList<string> arguments, string workingDirectory, bool breakawayFromJob)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException(NotWindows);

        // CreateProcessW may write to the command line, so it gets its own null-terminated buffer.
        var commandLine = (WindowsCommandLine.Join([executable, .. arguments]) + '\0').ToCharArray();
        var flags = CREATE_NO_WINDOW | CREATE_UNICODE_ENVIRONMENT | (breakawayFromJob ? CREATE_BREAKAWAY_FROM_JOB : 0);
        var startup = new STARTUPINFOW { cb = (uint)sizeof(STARTUPINFOW) };
        PROCESS_INFORMATION process;
        fixed (char* app = executable)
        fixed (char* line = commandLine)
        fixed (char* cwd = workingDirectory)
        {
            if (CreateProcessW(app, line, IntPtr.Zero, IntPtr.Zero, 0, flags, IntPtr.Zero, cwd, &startup, &process) == 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        CloseHandle(process.hThread);
        CloseHandle(process.hProcess);
        return (int)process.dwProcessId;
    }

    // ----- native -----

    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_BREAKAWAY_OK = 0x00000800;
    private const uint JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK = 0x00001000;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint CREATE_BREAKAWAY_FROM_JOB = 0x01000000;
    private const uint CREATE_NO_WINDOW = 0x08000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
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
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOW
    {
        public uint cb;
        public IntPtr lpReserved, lpDesktop, lpTitle;
        public uint dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public ushort wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public uint dwProcessId, dwThreadId;
    }

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetCurrentProcess();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int IsProcessInJob(IntPtr process, IntPtr job, out int result);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static unsafe partial int QueryInformationJobObject(IntPtr job, int infoClass, void* info, uint length, IntPtr returnLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static unsafe partial int CreateProcessW(char* applicationName, char* commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, int inheritHandles, uint creationFlags, IntPtr environment, char* currentDirectory,
        STARTUPINFOW* startupInfo, PROCESS_INFORMATION* processInformation);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int CloseHandle(IntPtr handle);
}
