using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Instella.Installer.Runtime.Core.Processes;

/// <summary>How this process's Windows token relates to UAC (<c>TOKEN_ELEVATION_TYPE</c>).</summary>
internal enum TokenElevationType
{
    /// <summary>No split token: UAC is off, or this is the built-in Administrator. There is no other token to switch to.</summary>
    Default = 1,

    /// <summary>The elevated half of a UAC pair; the same user also has an unelevated token.</summary>
    Full = 2,

    /// <summary>The unelevated half of a UAC pair.</summary>
    Limited = 3,
}

/// <summary>Reads the elevation type of the current process's token.</summary>
[SupportedOSPlatform("windows")]
internal static partial class TokenElevation
{
    private const uint TokenQuery = 0x0008;
    private const int TokenElevationTypeClass = 18;

    /// <summary>The current process's elevation type, or <see cref="TokenElevationType.Default"/> when it cannot be read.</summary>
    public static TokenElevationType Current()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out var token)) return TokenElevationType.Default;
        try
        {
            return GetTokenInformation(token, TokenElevationTypeClass, out var type, sizeof(int), out _)
                ? (TokenElevationType)type
                : TokenElevationType.Default;
        }
        finally
        {
            CloseHandle(token);
        }
    }

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetCurrentProcess();

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass, out int tokenInformation,
        int tokenInformationLength, out int returnLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);
}
