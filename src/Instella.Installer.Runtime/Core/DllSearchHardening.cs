using System;
using System.Runtime.InteropServices;

namespace Instella.Installer.Runtime.Core;

/// <summary>
/// Restricts the process-wide DLL search order to System32 before any UI or COM work, so
/// libraries loaded indirectly (by the OS, COM or the CLR) cannot come from the installer's
/// own folder either. The SDK does not do this: inside the user's app the DLL
/// search policy belongs to the app.
/// </summary>
internal static partial class DllSearchHardening
{
    private const uint LOAD_LIBRARY_SEARCH_SYSTEM32 = 0x00000800;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetDefaultDllDirectories(uint directoryFlags);

    /// <summary>Applies the restriction on Windows; a no-op elsewhere.</summary>
    public static void Apply()
    {
        if (OperatingSystem.IsWindows())
            SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_SYSTEM32);
    }
}
