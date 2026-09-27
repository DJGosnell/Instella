using System.Runtime.InteropServices;

namespace Instella.Installer.Runtime.UI.MacOS;

/// <summary>
/// P/Invoke declarations for libobjc.dylib and libSystem.B.dylib.
/// Provides access to the Objective-C runtime for AppKit interop.
/// </summary>
internal static partial class ObjC
{
    private const string Lib = "libobjc.dylib";

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint objc_getClass(string name);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint sel_registerName(string name);

    // objc_msgSend overloads by argument/return type

    [LibraryImport(Lib, EntryPoint = "objc_msgSend")]
    internal static partial nint msgSend(nint receiver, nint selector);

    [LibraryImport(Lib, EntryPoint = "objc_msgSend")]
    internal static partial nint msgSend(nint receiver, nint selector, nint arg1);

    [LibraryImport(Lib, EntryPoint = "objc_msgSend")]
    internal static partial nint msgSend(nint receiver, nint selector, nint arg1, nint arg2);

    [LibraryImport(Lib, EntryPoint = "objc_msgSend")]
    internal static partial nint msgSend(nint receiver, nint selector, nint arg1, nint arg2, nint arg3, nint arg4);

    [LibraryImport(Lib, EntryPoint = "objc_msgSend")]
    internal static partial void msgSendVoid(nint receiver, nint selector);

    [LibraryImport(Lib, EntryPoint = "objc_msgSend")]
    internal static partial void msgSendVoid(nint receiver, nint selector, nint arg1);

    [LibraryImport(Lib, EntryPoint = "objc_msgSend")]
    internal static partial void msgSendVoid(nint receiver, nint selector, int arg1);

    [LibraryImport(Lib, EntryPoint = "objc_msgSend")]
    internal static partial void msgSendVoid(nint receiver, nint selector, double arg1);

    [LibraryImport(Lib, EntryPoint = "objc_msgSend")]
    internal static partial void msgSendVoid(nint receiver, nint selector, nint arg1, nint arg2);

    [LibraryImport(Lib, EntryPoint = "objc_msgSend")]
    internal static partial double msgSendDouble(nint receiver, nint selector);

    [LibraryImport(Lib, EntryPoint = "objc_msgSend")]
    internal static partial int msgSendInt(nint receiver, nint selector);

    [LibraryImport(Lib, EntryPoint = "objc_msgSend")]
    internal static partial nint msgSendInt(nint receiver, nint selector, int arg1);

    // NSRect-based overloads
    [LibraryImport(Lib, EntryPoint = "objc_msgSend")]
    internal static partial nint msgSendRect(nint receiver, nint selector, NSRect frame, uint styleMask, uint backing, int defer);

    [LibraryImport(Lib, EntryPoint = "objc_msgSend")]
    internal static partial nint msgSendRect(nint receiver, nint selector, NSRect frame);

    [LibraryImport(Lib, EntryPoint = "objc_msgSend")]
    internal static partial NSRect msgSendRectRet(nint receiver, nint selector);

    // Class pair creation
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint objc_allocateClassPair(nint superclass, string name, nuint extraBytes);

    [LibraryImport(Lib)]
    internal static partial void objc_registerClassPair(nint cls);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int class_addMethod(nint cls, nint sel, nint imp, string types);
}

internal static partial class Dispatch
{
    [LibraryImport("libSystem.B.dylib", EntryPoint = "dispatch_get_main_queue")]
    internal static partial nint GetMainQueue();

    [LibraryImport("libSystem.B.dylib")]
    internal static partial void dispatch_async_f(nint queue, nint context, nint work);
}

[StructLayout(LayoutKind.Sequential)]
internal struct NSRect
{
    public double X, Y, Width, Height;

    public NSRect(double x, double y, double w, double h)
    {
        X = x; Y = y; Width = w; Height = h;
    }
}
