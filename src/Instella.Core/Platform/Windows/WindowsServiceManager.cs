using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Instella.Core.Platform.Windows;

/// <summary>
/// <see cref="IServiceManager"/> over the Windows Service Control Manager (advapi32). Own-process
/// services only. The SCM is opened with the least rights each call needs, so queries work
/// without elevation; every handle is closed through <see cref="ScHandle"/>.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsServiceManager : IServiceManager
{
    public Task<ServiceResult> CreateAsync(ServiceConfiguration configuration, char[]? password, CancellationToken ct)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(configuration);
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(Create(configuration, password));
        }
        finally
        {
            if (password is not null) Array.Clear(password);
        }
    }

    public Task<ServiceResult> ChangeConfigurationAsync(ServiceConfiguration configuration, char[]? password, CancellationToken ct)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(configuration);
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(ChangeConfiguration(configuration, password));
        }
        finally
        {
            if (password is not null) Array.Clear(password);
        }
    }

    public Task<ServiceResult<ServiceConfigurationInfo>> QueryConfigurationAsync(string name, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(QueryConfiguration(name));
    }

    public Task<ServiceResult<ServiceStatus>> QueryStatusAsync(string name, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ct.ThrowIfCancellationRequested();
        using var opened = Open(name, SERVICE_QUERY_STATUS, out var error);
        if (opened is null) return Task.FromResult(Failure<ServiceStatus>("open", name, error));
        if (!TryQueryStatus(opened.Service, out var status, out error))
            return Task.FromResult(Failure<ServiceStatus>("query the status of", name, error));
        return Task.FromResult(ServiceResult<ServiceStatus>.Ok(
            new ServiceStatus((ServiceState)status.dwCurrentState, (int)status.dwProcessId)));
    }

    public async Task<ServiceResult> StartAsync(string name, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ct.ThrowIfCancellationRequested();
        using var opened = Open(name, SERVICE_START | SERVICE_QUERY_STATUS, out var error);
        if (opened is null) return Failure("open", name, error);
        if (!TryQueryStatus(opened.Service, out var status, out error)) return Failure("query the status of", name, error);

        var state = (ServiceState)status.dwCurrentState;
        if (state == ServiceState.Running) return ServiceResult.Ok;
        if (state != ServiceState.StartPending && NativeMethods.StartServiceW(opened.Service, 0, 0) == 0)
        {
            error = Marshal.GetLastPInvokeError();
            if (error != ServiceErrors.AlreadyRunning) return Failure("start", name, error);
        }
        return await WaitAsync(opened.Service, name, ServiceState.Running, timeout, ct).ConfigureAwait(false);
    }

    public async Task<ServiceResult> StopAsync(string name, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ct.ThrowIfCancellationRequested();
        using var opened = Open(name, SERVICE_STOP | SERVICE_QUERY_STATUS | SERVICE_ENUMERATE_DEPENDENTS, out var error);
        if (opened is null) return Failure("open", name, error);
        if (!TryQueryStatus(opened.Service, out var status, out error)) return Failure("query the status of", name, error);
        if ((ServiceState)status.dwCurrentState == ServiceState.Stopped) return ServiceResult.Ok;

        // Running dependents first, in the order the SCM returns them (reverse start order).
        var dependents = ActiveDependents(opened.Service, out error);
        if (dependents is null) return Failure("list the dependents of", name, error);
        foreach (var dependent in dependents)
        {
            using var dep = Open(dependent, SERVICE_STOP | SERVICE_QUERY_STATUS, out error);
            if (dep is null)
            {
                if (ServiceErrors.Classify(error) == ServiceFailure.NotFound) continue;
                return Failure("open", dependent, error);
            }
            var stopped = await StopOneAsync(dep.Service, dependent, timeout, ct).ConfigureAwait(false);
            if (!stopped.Outcome.Success) return stopped;
        }
        return await StopOneAsync(opened.Service, name, timeout, ct).ConfigureAwait(false);
    }

    public Task<ServiceResult> DeleteAsync(string name, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ct.ThrowIfCancellationRequested();
        using var opened = Open(name, DELETE, out var error);
        if (opened is null) return Task.FromResult(Failure("open", name, error));
        if (NativeMethods.DeleteService(opened.Service) == 0)
            return Task.FromResult(Failure("delete", name, Marshal.GetLastPInvokeError()));
        return Task.FromResult(ServiceResult.Ok);
    }

    // ----- create / change -----

    private static unsafe ServiceResult Create(ServiceConfiguration c, char[]? password)
    {
        var binaryPath = c.BinaryPath;
        using var scm = NativeMethods.OpenSCManagerW(null, null, SC_MANAGER_CREATE_SERVICE);
        if (scm.IsInvalid) return Failure("open the service manager to create", c.Name, Marshal.GetLastPInvokeError());

        var nativePassword = CopyPassword(password);
        try
        {
            fixed (char* dependencies = MultiSz(c.Dependencies))
            {
                using var service = NativeMethods.CreateServiceW(scm, c.Name, c.DisplayName,
                    SERVICE_QUERY_CONFIG | SERVICE_CHANGE_CONFIG | SERVICE_START | DELETE,
                    SERVICE_WIN32_OWN_PROCESS, NativeStartType(c.StartType), SERVICE_ERROR_NORMAL,
                    binaryPath, null, 0, dependencies, c.AccountName, nativePassword);
                if (service.IsInvalid) return Failure("create", c.Name, Marshal.GetLastPInvokeError());

                var (operation, error) = WriteConfig2(service, c, creating: true);
                if (error != 0)
                {
                    // A half-configured service is not what was asked for: take it back out.
                    _ = NativeMethods.DeleteService(service);
                    return Failure(operation, c.Name, error);
                }
            }
            return ServiceResult.Ok;
        }
        finally
        {
            FreePassword(nativePassword, password?.Length ?? 0);
        }
    }

    private static unsafe ServiceResult ChangeConfiguration(ServiceConfiguration c, char[]? password)
    {
        var binaryPath = c.BinaryPath;
        // SERVICE_START: failure actions that restart the service need it on the handle.
        using var opened = Open(c.Name, SERVICE_QUERY_CONFIG | SERVICE_CHANGE_CONFIG | SERVICE_START, out var error);
        if (opened is null) return Failure("open", c.Name, error);

        var account = c.AccountName ?? LocalSystem;
        var nativePassword = CopyPassword(password);
        try
        {
            // No password: an empty one for the built-in accounts (they have none), otherwise null,
            // which keeps the existing password (virtual and managed accounts).
            fixed (char* empty = string.Empty)
            fixed (char* dependencies = MultiSz(c.Dependencies))
            {
                var passwordArg = nativePassword != null ? nativePassword
                    : IsBuiltInAccount(account) ? empty : null;
                if (NativeMethods.ChangeServiceConfigW(opened.Service, SERVICE_WIN32_OWN_PROCESS,
                        NativeStartType(c.StartType), SERVICE_ERROR_NORMAL, binaryPath, null, 0,
                        dependencies, account, passwordArg, c.DisplayName) == 0)
                    return Failure("change the configuration of", c.Name, Marshal.GetLastPInvokeError());
            }
        }
        finally
        {
            FreePassword(nativePassword, password?.Length ?? 0);
        }

        var (operation, config2Error) = WriteConfig2(opened.Service, c, creating: false);
        return config2Error == 0 ? ServiceResult.Ok : Failure(operation, c.Name, config2Error);
    }

    /// <summary>
    /// The settings only <c>ChangeServiceConfig2W</c> writes: SID type (always), description and
    /// failure actions (when set) and delayed auto-start (on change always, on create when delayed).
    /// Returns the failed operation and its Win32 error, or error 0.
    /// </summary>
    private static unsafe (string Operation, int Error) WriteConfig2(ScHandle service, ServiceConfiguration c, bool creating)
    {
        var sid = new SERVICE_SID_INFO { dwServiceSidType = (uint)c.SidType };
        if (NativeMethods.ChangeServiceConfig2W(service, SERVICE_CONFIG_SERVICE_SID_INFO, &sid) == 0)
            return ("set the SID type of", Marshal.GetLastPInvokeError());

        if (c.Description is not null)
        {
            fixed (char* text = c.Description)
            {
                var description = new SERVICE_DESCRIPTIONW { lpDescription = text };
                if (NativeMethods.ChangeServiceConfig2W(service, SERVICE_CONFIG_DESCRIPTION, &description) == 0)
                    return ("set the description of", Marshal.GetLastPInvokeError());
            }
        }

        var delayed = c.StartType == ServiceStartType.AutomaticDelayed;
        if (delayed || !creating)
        {
            var info = new SERVICE_DELAYED_AUTO_START_INFO { fDelayedAutostart = delayed ? 1 : 0 };
            if (NativeMethods.ChangeServiceConfig2W(service, SERVICE_CONFIG_DELAYED_AUTO_START_INFO, &info) == 0)
                return ("set delayed auto-start for", Marshal.GetLastPInvokeError());
        }

        if (c.FailureActions is { } failure)
        {
            // A non-null action array with zero actions clears them; null would leave them as they are.
            var actions = new SC_ACTION[Math.Max(failure.Actions.Count, 1)];
            for (var i = 0; i < failure.Actions.Count; i++)
                actions[i] = new SC_ACTION { Type = (int)failure.Actions[i].Type, Delay = ToUInt(failure.Actions[i].Delay.TotalMilliseconds) };
            fixed (SC_ACTION* pActions = actions)
            fixed (char* command = failure.Command)
            {
                var info = new SERVICE_FAILURE_ACTIONSW
                {
                    dwResetPeriod = failure.ResetPeriod == TimeSpan.MaxValue ? INFINITE : ToUInt(failure.ResetPeriod.TotalSeconds),
                    lpRebootMsg = null,
                    lpCommand = command,
                    cActions = (uint)failure.Actions.Count,
                    lpsaActions = pActions,
                };
                if (NativeMethods.ChangeServiceConfig2W(service, SERVICE_CONFIG_FAILURE_ACTIONS, &info) == 0)
                    return ("set the failure actions of", Marshal.GetLastPInvokeError());
            }
        }
        return (string.Empty, 0);
    }

    // ----- queries -----

    private static unsafe ServiceResult<ServiceConfigurationInfo> QueryConfiguration(string name)
    {
        using var opened = Open(name, SERVICE_QUERY_CONFIG, out var error);
        if (opened is null) return Failure<ServiceConfigurationInfo>("open", name, error);
        var service = opened.Service;

        string displayName, binaryPath;
        string? account;
        IReadOnlyList<string> dependencies;
        uint startType;
        var buffer = Read((b, size, out needed) => NativeMethods.QueryServiceConfigW(service, b, size, out needed), out error);
        if (buffer == null) return Failure<ServiceConfigurationInfo>("query the configuration of", name, error);
        try
        {
            var q = (QUERY_SERVICE_CONFIGW*)buffer;
            displayName = ReadString(q->lpDisplayName) ?? name;
            binaryPath = ReadString(q->lpBinaryPathName) ?? string.Empty;
            account = ReadString(q->lpServiceStartName);
            dependencies = ReadMultiSz(q->lpDependencies);
            startType = q->dwStartType;
        }
        finally
        {
            NativeMemory.Free(buffer);
        }

        var start = startType switch
        {
            SERVICE_DEMAND_START => ServiceStartType.Manual,
            SERVICE_DISABLED => ServiceStartType.Disabled,
            _ => ServiceStartType.Automatic,
        };
        if (startType == SERVICE_AUTO_START)
        {
            buffer = ReadConfig2(service, SERVICE_CONFIG_DELAYED_AUTO_START_INFO, out error);
            if (buffer == null) return Failure<ServiceConfigurationInfo>("query delayed auto-start for", name, error);
            if (((SERVICE_DELAYED_AUTO_START_INFO*)buffer)->fDelayedAutostart != 0) start = ServiceStartType.AutomaticDelayed;
            NativeMemory.Free(buffer);
        }

        buffer = ReadConfig2(service, SERVICE_CONFIG_DESCRIPTION, out error);
        if (buffer == null) return Failure<ServiceConfigurationInfo>("query the description of", name, error);
        var description = ReadString(((SERVICE_DESCRIPTIONW*)buffer)->lpDescription);
        NativeMemory.Free(buffer);
        if (description is { Length: 0 }) description = null;

        buffer = ReadConfig2(service, SERVICE_CONFIG_SERVICE_SID_INFO, out error);
        if (buffer == null) return Failure<ServiceConfigurationInfo>("query the SID type of", name, error);
        var sidType = (ServiceSidType)((SERVICE_SID_INFO*)buffer)->dwServiceSidType;
        NativeMemory.Free(buffer);

        buffer = ReadConfig2(service, SERVICE_CONFIG_FAILURE_ACTIONS, out error);
        if (buffer == null) return Failure<ServiceConfigurationInfo>("query the failure actions of", name, error);
        ServiceFailureActions? failureActions = null;
        try
        {
            var f = (SERVICE_FAILURE_ACTIONSW*)buffer;
            var command = ReadString(f->lpCommand);
            if (command is { Length: 0 }) command = null;
            if (f->cActions > 0 || command is not null)
            {
                var actions = new ServiceFailureAction[f->cActions];
                for (var i = 0; i < actions.Length; i++)
                    actions[i] = new ServiceFailureAction((ServiceFailureActionType)f->lpsaActions[i].Type,
                        TimeSpan.FromMilliseconds(f->lpsaActions[i].Delay));
                var reset = f->dwResetPeriod == INFINITE ? TimeSpan.MaxValue : TimeSpan.FromSeconds(f->dwResetPeriod);
                failureActions = new ServiceFailureActions(reset, actions, command);
            }
        }
        finally
        {
            NativeMemory.Free(buffer);
        }

        return ServiceResult<ServiceConfigurationInfo>.Ok(new ServiceConfigurationInfo(
            name, displayName, description, binaryPath, start, account, dependencies, failureActions, sidType));
    }

    private static unsafe byte* ReadConfig2(ScHandle service, uint level, out int error) =>
        Read((b, size, out needed) => NativeMethods.QueryServiceConfig2W(service, level, b, size, out needed), out error);

    private static unsafe bool TryQueryStatus(ScHandle service, out SERVICE_STATUS_PROCESS status, out int error)
    {
        SERVICE_STATUS_PROCESS s;
        if (NativeMethods.QueryServiceStatusEx(service, SC_STATUS_PROCESS_INFO, (byte*)&s, (uint)sizeof(SERVICE_STATUS_PROCESS), out _) == 0)
        {
            error = Marshal.GetLastPInvokeError();
            status = default;
            return false;
        }
        error = 0;
        status = s;
        return true;
    }

    private static unsafe List<string>? ActiveDependents(ScHandle service, out int error)
    {
        uint count = 0;
        var buffer = Read((b, size, out needed) =>
            NativeMethods.EnumDependentServicesW(service, SERVICE_ACTIVE, b, size, out needed, out count), out error);
        if (buffer == null) return null;
        try
        {
            var entries = (ENUM_SERVICE_STATUSW*)buffer;
            var names = new List<string>((int)count);
            for (var i = 0; i < count; i++)
                names.Add(ReadString(entries[i].lpServiceName) ?? string.Empty);
            return names;
        }
        finally
        {
            NativeMemory.Free(buffer);
        }
    }

    // ----- start / stop waiting -----

    private static async Task<ServiceResult> StopOneAsync(ScHandle service, string name, TimeSpan timeout, CancellationToken ct)
    {
        if (!TryQueryStatus(service, out var status, out var error)) return Failure("query the status of", name, error);
        var state = (ServiceState)status.dwCurrentState;
        if (state == ServiceState.Stopped) return ServiceResult.Ok;
        if (state != ServiceState.StopPending && !SendStop(service, out error))
        {
            if (error == ServiceErrors.NotActive) return ServiceResult.Ok;
            if (error != ERROR_SERVICE_CANNOT_ACCEPT_CTRL) return Failure("stop", name, error);
        }
        return await WaitAsync(service, name, ServiceState.Stopped, timeout, ct).ConfigureAwait(false);
    }

    private static unsafe bool SendStop(ScHandle service, out int error)
    {
        SERVICE_STATUS status;
        if (NativeMethods.ControlService(service, SERVICE_CONTROL_STOP, &status) == 0)
        {
            error = Marshal.GetLastPInvokeError();
            return false;
        }
        error = 0;
        return true;
    }

    /// <summary>
    /// Polls until the service reaches <paramref name="target"/>, sleeping a tenth of its wait hint
    /// (100 ms to 1 s). A start that ends Stopped fails with the service's exit code; past
    /// <paramref name="timeout"/> the result is <see cref="ServiceFailure.Timeout"/>.
    /// </summary>
    private static async Task<ServiceResult> WaitAsync(ScHandle service, string name, ServiceState target, TimeSpan timeout, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            if (!TryQueryStatus(service, out var status, out var error)) return Failure("query the status of", name, error);
            var state = (ServiceState)status.dwCurrentState;
            if (state == target) return ServiceResult.Ok;
            if (target == ServiceState.Running && state == ServiceState.Stopped)
            {
                var exitCode = status.dwWin32ExitCode == ERROR_SERVICE_SPECIFIC_ERROR
                    ? status.dwServiceSpecificExitCode : status.dwWin32ExitCode;
                return ServiceResult.Fail(ServiceFailure.Failed,
                    $"Service '{name}' stopped while starting (exit code {exitCode}).", (int)status.dwWin32ExitCode);
            }

            var remaining = timeout - clock.Elapsed;
            if (remaining <= TimeSpan.Zero)
                return ServiceResult.Fail(ServiceFailure.Timeout,
                    $"Service '{name}' did not reach {target} within {timeout.TotalSeconds:0.###} s (it is {state}).");

            var wait = TimeSpan.FromMilliseconds(Math.Clamp(status.dwWaitHint / 10.0, 100, 1000));
            await Task.Delay(wait < remaining ? wait : remaining, ct).ConfigureAwait(false);
        }
    }

    // ----- helpers -----

    private const string LocalSystem = "LocalSystem";

    private static bool IsBuiltInAccount(string account) =>
        account.Equals(LocalSystem, StringComparison.OrdinalIgnoreCase)
        || account.Equals(@".\LocalSystem", StringComparison.OrdinalIgnoreCase)
        || account.Equals(@"NT AUTHORITY\SYSTEM", StringComparison.OrdinalIgnoreCase)
        || account.Equals(@"NT AUTHORITY\LocalService", StringComparison.OrdinalIgnoreCase)
        || account.Equals(@"NT AUTHORITY\NetworkService", StringComparison.OrdinalIgnoreCase);

    private static uint NativeStartType(ServiceStartType type) => type switch
    {
        ServiceStartType.Manual => SERVICE_DEMAND_START,
        ServiceStartType.Disabled => SERVICE_DISABLED,
        _ => SERVICE_AUTO_START,
    };

    private static uint ToUInt(double value) => value <= 0 ? 0 : value >= uint.MaxValue ? uint.MaxValue - 1 : (uint)value;

    /// <summary>The names NUL-separated; the string's own terminator supplies the closing double NUL.</summary>
    private static string MultiSz(IReadOnlyList<string> values) =>
        values.Count == 0 ? "\0" : string.Join('\0', values) + "\0";

    private static unsafe string? ReadString(char* value) => value == null ? null : new string(value);

    private static unsafe List<string> ReadMultiSz(char* value)
    {
        var list = new List<string>();
        if (value == null) return list;
        while (*value != '\0')
        {
            var item = new string(value);
            list.Add(item);
            value += item.Length + 1;
        }
        return list;
    }

    /// <summary>The password in native memory, NUL-terminated; null when there is none.</summary>
    private static unsafe char* CopyPassword(char[]? password)
    {
        if (password is null) return null;
        var native = (char*)NativeMemory.Alloc((nuint)password.Length + 1, sizeof(char));
        password.AsSpan().CopyTo(new Span<char>(native, password.Length));
        native[password.Length] = '\0';
        return native;
    }

    /// <summary>Zeroes, then frees, a buffer from <see cref="CopyPassword"/>.</summary>
    private static unsafe void FreePassword(char* native, int length)
    {
        if (native == null) return;
        NativeMemory.Clear(native, ((nuint)length + 1) * sizeof(char));
        NativeMemory.Free(native);
    }

    private unsafe delegate int NativeQuery(byte* buffer, uint size, out uint needed);

    /// <summary>
    /// The two-call pattern for variable-size queries: retries with the size the API asks for.
    /// Returns a native buffer the caller frees with <see cref="NativeMemory.Free"/>, or null with the error.
    /// </summary>
    private static unsafe byte* Read(NativeQuery query, out int error)
    {
        uint size = 1024;
        var buffer = (byte*)NativeMemory.Alloc(size);
        while (true)
        {
            if (query(buffer, size, out var needed) != 0)
            {
                error = 0;
                return buffer;
            }
            error = Marshal.GetLastPInvokeError();
            NativeMemory.Free(buffer);
            if ((error != ERROR_INSUFFICIENT_BUFFER && error != ERROR_MORE_DATA) || needed <= size) return null;
            size = needed;
            buffer = (byte*)NativeMemory.Alloc(size);
        }
    }

    private static Opened? Open(string name, uint access, out int error)
    {
        var scm = NativeMethods.OpenSCManagerW(null, null, SC_MANAGER_CONNECT);
        if (scm.IsInvalid)
        {
            error = Marshal.GetLastPInvokeError();
            scm.Dispose();
            return null;
        }
        var service = NativeMethods.OpenServiceW(scm, name, access);
        if (service.IsInvalid)
        {
            error = Marshal.GetLastPInvokeError();
            service.Dispose();
            scm.Dispose();
            return null;
        }
        error = 0;
        return new Opened(scm, service);
    }

    private static string Message(string operation, string name, int error) =>
        $"Could not {operation} service '{name}': {Marshal.GetPInvokeErrorMessage(error)} (Win32 error {error}).";

    private static ServiceResult Failure(string operation, string name, int error) =>
        ServiceResult.Fail(ServiceErrors.Classify(error), Message(operation, name, error), error);

    private static ServiceResult<T> Failure<T>(string operation, string name, int error) =>
        ServiceResult<T>.Fail(ServiceErrors.Classify(error), Message(operation, name, error), error);

    private sealed class Opened(ScHandle scm, ScHandle service) : IDisposable
    {
        public ScHandle Service { get; } = service;

        public void Dispose()
        {
            Service.Dispose();
            scm.Dispose();
        }
    }

    /// <summary>An SCM or service handle, closed with <c>CloseServiceHandle</c>.</summary>
    private sealed class ScHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public ScHandle() : base(ownsHandle: true) { }

        protected override bool ReleaseHandle() => NativeMethods.CloseServiceHandle(handle) != 0;
    }

    // ----- native -----

    private const uint SC_MANAGER_CONNECT = 0x0001;
    private const uint SC_MANAGER_CREATE_SERVICE = 0x0002;
    private const uint SERVICE_QUERY_CONFIG = 0x0001;
    private const uint SERVICE_CHANGE_CONFIG = 0x0002;
    private const uint SERVICE_QUERY_STATUS = 0x0004;
    private const uint SERVICE_ENUMERATE_DEPENDENTS = 0x0008;
    private const uint SERVICE_START = 0x0010;
    private const uint SERVICE_STOP = 0x0020;
    private const uint DELETE = 0x00010000;
    private const uint SERVICE_WIN32_OWN_PROCESS = 0x00000010;
    private const uint SERVICE_ERROR_NORMAL = 0x00000001;
    private const uint SERVICE_AUTO_START = 0x00000002;
    private const uint SERVICE_DEMAND_START = 0x00000003;
    private const uint SERVICE_DISABLED = 0x00000004;
    private const uint SERVICE_CONFIG_DESCRIPTION = 1;
    private const uint SERVICE_CONFIG_FAILURE_ACTIONS = 2;
    private const uint SERVICE_CONFIG_DELAYED_AUTO_START_INFO = 3;
    private const uint SERVICE_CONFIG_SERVICE_SID_INFO = 5;
    private const uint SERVICE_CONTROL_STOP = 0x00000001;
    private const uint SERVICE_ACTIVE = 0x00000001;
    private const uint INFINITE = 0xFFFFFFFF;
    private const int SC_STATUS_PROCESS_INFO = 0;
    private const int ERROR_INSUFFICIENT_BUFFER = 122;
    private const int ERROR_MORE_DATA = 234;
    private const int ERROR_SERVICE_CANNOT_ACCEPT_CTRL = 1061;
    private const int ERROR_SERVICE_SPECIFIC_ERROR = 1066;

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct QUERY_SERVICE_CONFIGW
    {
        public uint dwServiceType;
        public uint dwStartType;
        public uint dwErrorControl;
        public char* lpBinaryPathName;
        public char* lpLoadOrderGroup;
        public uint dwTagId;
        public char* lpDependencies;
        public char* lpServiceStartName;
        public char* lpDisplayName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_STATUS
    {
        public uint dwServiceType;
        public uint dwCurrentState;
        public uint dwControlsAccepted;
        public uint dwWin32ExitCode;
        public uint dwServiceSpecificExitCode;
        public uint dwCheckPoint;
        public uint dwWaitHint;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_STATUS_PROCESS
    {
        public uint dwServiceType;
        public uint dwCurrentState;
        public uint dwControlsAccepted;
        public uint dwWin32ExitCode;
        public uint dwServiceSpecificExitCode;
        public uint dwCheckPoint;
        public uint dwWaitHint;
        public uint dwProcessId;
        public uint dwServiceFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct ENUM_SERVICE_STATUSW
    {
        public char* lpServiceName;
        public char* lpDisplayName;
        public SERVICE_STATUS ServiceStatus;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SC_ACTION
    {
        public int Type;
        public uint Delay;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct SERVICE_FAILURE_ACTIONSW
    {
        public uint dwResetPeriod;
        public char* lpRebootMsg;
        public char* lpCommand;
        public uint cActions;
        public SC_ACTION* lpsaActions;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct SERVICE_DESCRIPTIONW
    {
        public char* lpDescription;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_DELAYED_AUTO_START_INFO
    {
        public int fDelayedAutostart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_SID_INFO
    {
        public uint dwServiceSidType;
    }

    private static unsafe partial class NativeMethods
    {
        private const string Advapi32 = "advapi32.dll";

        [LibraryImport(Advapi32, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        public static partial ScHandle OpenSCManagerW(string? lpMachineName, string? lpDatabaseName, uint dwDesiredAccess);

        [LibraryImport(Advapi32, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        public static partial ScHandle OpenServiceW(ScHandle hSCManager, string lpServiceName, uint dwDesiredAccess);

        [LibraryImport(Advapi32, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        public static partial ScHandle CreateServiceW(
            ScHandle hSCManager, string lpServiceName, string lpDisplayName, uint dwDesiredAccess,
            uint dwServiceType, uint dwStartType, uint dwErrorControl, string lpBinaryPathName,
            string? lpLoadOrderGroup, nint lpdwTagId, char* lpDependencies, string? lpServiceStartName,
            char* lpPassword);

        [LibraryImport(Advapi32, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        public static partial int ChangeServiceConfigW(
            ScHandle hService, uint dwServiceType, uint dwStartType, uint dwErrorControl,
            string? lpBinaryPathName, string? lpLoadOrderGroup, nint lpdwTagId, char* lpDependencies,
            string? lpServiceStartName, char* lpPassword, string? lpDisplayName);

        [LibraryImport(Advapi32, SetLastError = true)]
        public static partial int ChangeServiceConfig2W(ScHandle hService, uint dwInfoLevel, void* lpInfo);

        [LibraryImport(Advapi32, SetLastError = true)]
        public static partial int QueryServiceConfigW(ScHandle hService, byte* lpServiceConfig, uint cbBufSize, out uint pcbBytesNeeded);

        [LibraryImport(Advapi32, SetLastError = true)]
        public static partial int QueryServiceConfig2W(ScHandle hService, uint dwInfoLevel, byte* lpBuffer, uint cbBufSize, out uint pcbBytesNeeded);

        [LibraryImport(Advapi32, SetLastError = true)]
        public static partial int QueryServiceStatusEx(ScHandle hService, int infoLevel, byte* lpBuffer, uint cbBufSize, out uint pcbBytesNeeded);

        [LibraryImport(Advapi32, SetLastError = true)]
        public static partial int StartServiceW(ScHandle hService, uint dwNumServiceArgs, nint lpServiceArgVectors);

        [LibraryImport(Advapi32, SetLastError = true)]
        public static partial int ControlService(ScHandle hService, uint dwControl, SERVICE_STATUS* lpServiceStatus);

        [LibraryImport(Advapi32, SetLastError = true)]
        public static partial int EnumDependentServicesW(
            ScHandle hService, uint dwServiceState, byte* lpServices, uint cbBufSize,
            out uint pcbBytesNeeded, out uint lpServicesReturned);

        [LibraryImport(Advapi32, SetLastError = true)]
        public static partial int DeleteService(ScHandle hService);

        [LibraryImport(Advapi32, SetLastError = true)]
        public static partial int CloseServiceHandle(nint hSCObject);
    }
}
