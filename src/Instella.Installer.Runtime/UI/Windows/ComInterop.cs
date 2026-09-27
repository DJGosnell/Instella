using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Instella.Installer.Runtime.UI.Windows;

[GeneratedComInterface]
[Guid("d57c7288-d4ad-4768-be02-9d969532d960")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal partial interface IFileOpenDialog
{
    // IModalWindow
    [PreserveSig]
    int Show(nint hwndOwner);

    // IFileDialog
    void SetFileTypes(uint cFileTypes, nint rgFilterSpec);
    void SetFileTypeIndex(uint iFileType);
    void GetFileTypeIndex(out uint piFileType);
    void Advise(nint pfde, out uint pdwCookie);
    void Unadvise(uint dwCookie);
    void SetOptions(uint fos);
    void GetOptions(out uint pfos);
    void SetDefaultFolder(IShellItem psi);
    void SetFolder(IShellItem psi);
    void GetFolder(out IShellItem ppsi);
    void GetCurrentSelection(out IShellItem ppsi);
    void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
    void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
    void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
    void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
    void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
    void GetResult(out IShellItem ppsi);
    void AddPlace(IShellItem psi, int fdap);
    void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
    void Close(int hr);
    void SetClientGuid(in Guid guid);
    void ClearClientData();
    void SetFilter(nint pFilter);
}

[GeneratedComInterface]
[Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal partial interface IShellItem
{
    void BindToHandler(nint pbc, in Guid bhid, in Guid riid, out nint ppv);
    void GetParent(out IShellItem ppsi);
    void GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
    void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
    void Compare(IShellItem psi, uint hint, out int piOrder);
}

internal static partial class FolderPickerDialog
{
    private static readonly Guid CLSID_FileOpenDialog = new("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7");
    private static readonly Guid IID_IFileOpenDialog = new("d57c7288-d4ad-4768-be02-9d969532d960");

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid rclsid, nint pUnkOuter, uint dwClsContext, in Guid riid, out nint ppv);

    internal static string? ShowFolderPicker(nint hwndOwner)
    {
        try
        {
            var hr = CoCreateInstance(in CLSID_FileOpenDialog, 0, CLSCTX.INPROC_SERVER, in IID_IFileOpenDialog, out var pDialog);
            if (hr < 0) return null;

            var wrappers = new StrategyBasedComWrappers();
            var dialog = (IFileOpenDialog)wrappers.GetOrCreateObjectForComInstance(pDialog, CreateObjectFlags.None);

            dialog.GetOptions(out var options);
            dialog.SetOptions(options | FOS.PICKFOLDERS);
            dialog.SetTitle("Select Installation Folder");

            hr = dialog.Show(hwndOwner);
            if (hr < 0) return null;

            dialog.GetResult(out var item);
            item.GetDisplayName(SIGDN.FILESYSPATH, out var path);
            return path;
        }
        catch
        {
            return null;
        }
    }
}
