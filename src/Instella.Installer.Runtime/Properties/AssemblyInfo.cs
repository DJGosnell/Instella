using System.Runtime.InteropServices;

// Every DllImport/LibraryImport in the runtime resolves from System32 only (honoured by
// NativeAOT). The installer usually runs from the Downloads folder, and several libraries it
// loads (gdiplus, uxtheme, dwmapi, shcore, comctl32) are not KnownDLLs, so a DLL planted next
// to the installer would otherwise be loaded.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
