using System;
using System.Runtime.InteropServices;

namespace Instella.Installer.Runtime.Core;

/// <summary>
/// Installers are GUI-subsystem programs, so started from a terminal they have no console and
/// <c>--help</c> / <c>--list-versions</c> output would go nowhere. Attaching to the parent's
/// console makes it appear there; redirected output (a pipe or file) is left alone.
/// </summary>
internal static partial class ConsoleAttach
{
    private const int StdOutputHandle = -11;
    private const uint AttachParentProcess = 0xFFFFFFFF;

    [LibraryImport("kernel32.dll")]
    private static partial nint GetStdHandle(int nStdHandle);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(uint dwProcessId);

    /// <summary>Attaches to the parent's console when standard output goes nowhere (Windows only).</summary>
    public static void ToParentConsole()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (GetStdHandle(StdOutputHandle) is 0 or -1)
            AttachConsole(AttachParentProcess);
    }
}
