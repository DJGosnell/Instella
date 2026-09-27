using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Instella.Core.Platform.Windows;

/// <summary>
/// Creates Windows shortcuts (.lnk files) by invoking the
/// <c>IShellLinkW</c> + <c>IPersistFile</c> COM interfaces directly through
/// vtable function pointers.
/// </summary>
/// <remarks>
/// <para>
/// We deliberately avoid the <c>[ComImport]</c> + <c>Marshal.GetObjectForIUnknown</c>
/// pattern: under NativeAOT, the built-in COM RCW machinery can be trimmed
/// or behave inconsistently across apartment boundaries, which surfaced as
/// shortcut creation succeeding (no exception, returns true) but no .lnk
/// file ever appearing on disk.
/// </para>
/// <para>
/// Direct vtable dispatch via <c>delegate* unmanaged</c> function pointers
/// is the canonical AOT-safe COM pattern: it lowers to a single indirect
/// call with no marshalling layer in between, identical to what a C
/// program would emit.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed unsafe partial class WindowsShortcutCreator
{
    // CLSID_ShellLink, IID_IShellLinkW, IID_IPersistFile.
    private static readonly Guid CLSID_ShellLink = new("00021401-0000-0000-C000-000000000046");
    private static readonly Guid IID_IShellLinkW = new("000214F9-0000-0000-C000-000000000046");
    private static readonly Guid IID_IPersistFile = new("0000010B-0000-0000-C000-000000000046");

    private const uint CLSCTX_INPROC_SERVER = 1;
    private const uint COINIT_APARTMENTTHREADED = 2;

    // IShellLinkW vtable indices (after IUnknown at 0-2).
    private const int Vt_SetDescription = 7;
    private const int Vt_SetWorkingDirectory = 9;
    private const int Vt_SetArguments = 11;
    private const int Vt_SetIconLocation = 17;
    private const int Vt_SetPath = 20;

    // IPersistFile vtable indices (after IUnknown 0-2 and IPersist::GetClassID at 3).
    private const int Vt_PersistFile_Load = 5;
    private const int Vt_PersistFile_Save = 6;

    // IUnknown vtable indices.
    private const int Vt_QueryInterface = 0;
    private const int Vt_Release = 2;

    public void Create(string shortcutPath, ShortcutInfo info)
    {
        // Ensure the .lnk's parent directory exists. The Shell will create
        // the file but won't create a missing parent.
        var parent = Path.GetDirectoryName(shortcutPath);
        if (!string.IsNullOrEmpty(parent))
            Directory.CreateDirectory(parent);

        // Initialize COM as STA on this thread. Idempotent — if the runtime
        // already STA-init'd the thread (we call this from a Thread with
        // SetApartmentState(STA)), this is a refcount bump returning S_FALSE.
        var coHr = CoInitializeEx(IntPtr.Zero, COINIT_APARTMENTTHREADED);
        var ownsCoInit = coHr >= 0; // S_OK or S_FALSE both pair with CoUninitialize.

        try
        {
            IntPtr pShellLink;
            var hr = CoCreateInstance(in CLSID_ShellLink, IntPtr.Zero,
                CLSCTX_INPROC_SERVER, in IID_IShellLinkW, out pShellLink);
            ThrowIfFailed(hr, "CoCreateInstance(IShellLinkW)");

            try
            {
                CallSetW(pShellLink, Vt_SetPath, info.TargetPath);

                if (!string.IsNullOrEmpty(info.WorkingDirectory))
                    CallSetW(pShellLink, Vt_SetWorkingDirectory, info.WorkingDirectory);

                if (!string.IsNullOrEmpty(info.Description))
                    CallSetW(pShellLink, Vt_SetDescription, info.Description);

                if (!string.IsNullOrEmpty(info.Arguments))
                    CallSetW(pShellLink, Vt_SetArguments, info.Arguments);

                CallSetIconLocation(pShellLink, IconLocationFor(info.IconPath, info.TargetPath), 0);

                IntPtr pPersistFile = QueryInterface(pShellLink, in IID_IPersistFile);
                try
                {
                    CallSaveW(pPersistFile, shortcutPath, fRemember: 1);
                }
                finally
                {
                    Release(pPersistFile);
                }
            }
            finally
            {
                Release(pShellLink);
            }
        }
        finally
        {
            if (ownsCoInit) CoUninitialize();
        }
    }

    public void UpdateTarget(string shortcutPath, string newTarget)
    {
        var coHr = CoInitializeEx(IntPtr.Zero, COINIT_APARTMENTTHREADED);
        var ownsCoInit = coHr >= 0;

        try
        {
            IntPtr pShellLink;
            var hr = CoCreateInstance(in CLSID_ShellLink, IntPtr.Zero,
                CLSCTX_INPROC_SERVER, in IID_IShellLinkW, out pShellLink);
            ThrowIfFailed(hr, "CoCreateInstance(IShellLinkW)");

            try
            {
                IntPtr pPersistFile = QueryInterface(pShellLink, in IID_IPersistFile);
                try
                {
                    CallLoadW(pPersistFile, shortcutPath, dwMode: 0);
                    CallSetW(pShellLink, Vt_SetPath, newTarget);
                    CallSaveW(pPersistFile, shortcutPath, fRemember: 1);
                }
                finally
                {
                    Release(pPersistFile);
                }
            }
            finally
            {
                Release(pShellLink);
            }
        }
        finally
        {
            if (ownsCoInit) CoUninitialize();
        }
    }

    public void Delete(string shortcutPath)
    {
        if (File.Exists(shortcutPath))
            File.Delete(shortcutPath);
    }

    // ---- vtable dispatch helpers --------------------------------------

    private static void CallSetW(IntPtr pUnk, int vtableIndex, string value)
    {
        var vtbl = *(IntPtr**)pUnk;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, char*, int>)vtbl[vtableIndex];
        fixed (char* p = value)
        {
            ThrowIfFailed(fn(pUnk, p), $"IShellLinkW vtable[{vtableIndex}]");
        }
    }

    private static void CallSetIconLocation(IntPtr pShellLink, string iconPath, int iconIndex)
    {
        var vtbl = *(IntPtr**)pShellLink;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, char*, int, int>)vtbl[Vt_SetIconLocation];
        fixed (char* p = iconPath)
        {
            ThrowIfFailed(fn(pShellLink, p, iconIndex), "IShellLinkW.SetIconLocation");
        }
    }

    private static void CallSaveW(IntPtr pPersistFile, string fileName, int fRemember)
    {
        var vtbl = *(IntPtr**)pPersistFile;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, char*, int, int>)vtbl[Vt_PersistFile_Save];
        fixed (char* p = fileName)
        {
            ThrowIfFailed(fn(pPersistFile, p, fRemember), "IPersistFile.Save");
        }
    }

    private static void CallLoadW(IntPtr pPersistFile, string fileName, int dwMode)
    {
        var vtbl = *(IntPtr**)pPersistFile;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, char*, int, int>)vtbl[Vt_PersistFile_Load];
        fixed (char* p = fileName)
        {
            ThrowIfFailed(fn(pPersistFile, p, dwMode), "IPersistFile.Load");
        }
    }

    private static IntPtr QueryInterface(IntPtr pUnk, in Guid iid)
    {
        var vtbl = *(IntPtr**)pUnk;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)vtbl[Vt_QueryInterface];
        IntPtr ppv;
        fixed (Guid* iidPtr = &iid)
        {
            ThrowIfFailed(fn(pUnk, iidPtr, &ppv), "IUnknown.QueryInterface");
        }
        return ppv;
    }

    private static void Release(IntPtr pUnk)
    {
        if (pUnk == IntPtr.Zero) return;
        var vtbl = *(IntPtr**)pUnk;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint>)vtbl[Vt_Release];
        fn(pUnk);
    }

    private static void ThrowIfFailed(int hr, string operation)
    {
        if (hr < 0)
            throw new Win32Exception(hr, $"{operation} failed (HRESULT 0x{hr:X8})");
    }

    /// <summary>
    /// The shortcut's icon: the full icon path it is given, used as is. The caller
    /// (<c>ExecutableResolver.Icon</c>) decides whether there is one: on a first install the
    /// app icon is still staged when the shortcut is made, and reaches this path at the commit,
    /// so an existence check here would wrongly fall back to the exe icon. No path, or a relative one
    /// (a build-machine path), uses the target exe (index 0), whose embedded icon Windows shows.
    /// </summary>
    internal static string IconLocationFor(string? iconPath, string targetPath) =>
        !string.IsNullOrWhiteSpace(iconPath) && Path.IsPathRooted(iconPath) ? iconPath : targetPath;

    // ---- P/Invoke surface ---------------------------------------------

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(
        in Guid rclsid, IntPtr pUnkOuter, uint dwClsContext, in Guid riid, out IntPtr ppv);

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

    [LibraryImport("ole32.dll")]
    private static partial void CoUninitialize();
}
