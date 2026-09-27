using System.Runtime.InteropServices;

namespace Instella.Installer.Runtime.UI.MacOS;

/// <summary>
/// Thin helpers wrapping raw objc_msgSend calls for readability.
/// </summary>
internal static class NS
{
    internal static nint Class(string name) => ObjC.objc_getClass(name);
    internal static nint Sel(string name) => ObjC.sel_registerName(name);

    internal static nint Alloc(string className) =>
        ObjC.msgSend(Class(className), Sel("alloc"));

    internal static nint StringNew(string s)
    {
        var utf8 = Marshal.StringToCoTaskMemUTF8(s);
        var nsStr = ObjC.msgSend(ObjC.msgSend(Class("NSString"), Sel("alloc")), Sel("initWithUTF8String:"), utf8);
        Marshal.FreeCoTaskMem(utf8);
        return nsStr;
    }

    internal static string StringRead(nint nsString)
    {
        if (nsString == 0) return "";
        var ptr = ObjC.msgSend(nsString, Sel("UTF8String"));
        return Marshal.PtrToStringUTF8(ptr) ?? "";
    }

    internal static void Release(nint obj)
    {
        if (obj != 0) ObjC.msgSendVoid(obj, Sel("release"));
    }

    // NSNumber
    internal static nint NumberWithBool(bool value) =>
        ObjC.msgSend(Class("NSNumber"), Sel("numberWithBool:"), value ? 1 : 0);

    // NSApplication
    internal static nint SharedApplication() =>
        ObjC.msgSend(Class("NSApplication"), Sel("sharedApplication"));

    internal static void ActivateApp(nint app)
    {
        ObjC.msgSendVoid(app, Sel("setActivationPolicy:"), 0); // NSApplicationActivationPolicyRegular
        ObjC.msgSendVoid(app, Sel("activateIgnoringOtherApps:"), 1);
    }

    internal static void RunApp(nint app) =>
        ObjC.msgSendVoid(app, Sel("run"));

    internal static void StopApp(nint app, nint sender) =>
        ObjC.msgSendVoid(app, Sel("stop:"), sender);

    // Window creation
    internal static nint CreateWindow(NSRect frame, uint styleMask)
    {
        var window = Alloc("NSWindow");
        return ObjC.msgSendRect(window, Sel("initWithContentRect:styleMask:backing:defer:"),
            frame, styleMask, 2 /* NSBackingStoreBuffered */, 0);
    }

    // NSTextField (label)
    internal static nint CreateLabel(string text, NSRect frame)
    {
        var field = ObjC.msgSendRect(Alloc("NSTextField"), Sel("initWithFrame:"), frame);
        var nsText = StringNew(text);
        ObjC.msgSendVoid(field, Sel("setStringValue:"), nsText);
        Release(nsText);
        ObjC.msgSendVoid(field, Sel("setBezeled:"), 0);
        ObjC.msgSendVoid(field, Sel("setDrawsBackground:"), 0);
        ObjC.msgSendVoid(field, Sel("setEditable:"), 0);
        ObjC.msgSendVoid(field, Sel("setSelectable:"), 0);
        return field;
    }

    // NSTextField (editable)
    internal static nint CreateTextField(string text, NSRect frame)
    {
        var field = ObjC.msgSendRect(Alloc("NSTextField"), Sel("initWithFrame:"), frame);
        var nsText = StringNew(text);
        ObjC.msgSendVoid(field, Sel("setStringValue:"), nsText);
        Release(nsText);
        ObjC.msgSendVoid(field, Sel("setBezeled:"), 1);
        ObjC.msgSendVoid(field, Sel("setEditable:"), 1);
        return field;
    }

    // NSButton (push button)
    internal static nint CreateButton(string title, NSRect frame)
    {
        var btn = ObjC.msgSendRect(Alloc("NSButton"), Sel("initWithFrame:"), frame);
        var nsTitle = StringNew(title);
        ObjC.msgSendVoid(btn, Sel("setTitle:"), nsTitle);
        Release(nsTitle);
        ObjC.msgSendVoid(btn, Sel("setBezelStyle:"), 1); // NSBezelStyleRounded
        return btn;
    }

    // NSButton (checkbox)
    internal static nint CreateCheckbox(string title, NSRect frame, bool isChecked)
    {
        var btn = ObjC.msgSendRect(Alloc("NSButton"), Sel("initWithFrame:"), frame);
        var nsTitle = StringNew(title);
        ObjC.msgSendVoid(btn, Sel("setTitle:"), nsTitle);
        Release(nsTitle);
        ObjC.msgSendVoid(btn, Sel("setButtonType:"), 3); // NSSwitchButton
        ObjC.msgSendVoid(btn, Sel("setState:"), isChecked ? 1 : 0);
        return btn;
    }

    // NSProgressIndicator
    internal static nint CreateProgressBar(NSRect frame)
    {
        var pbar = ObjC.msgSendRect(Alloc("NSProgressIndicator"), Sel("initWithFrame:"), frame);
        ObjC.msgSendVoid(pbar, Sel("setStyle:"), 0); // NSProgressIndicatorStyleBar
        ObjC.msgSendVoid(pbar, Sel("setMinValue:"), 0.0);
        ObjC.msgSendVoid(pbar, Sel("setMaxValue:"), 100.0);
        ObjC.msgSendVoid(pbar, Sel("setIndeterminate:"), 0);
        return pbar;
    }

    // View helpers
    internal static nint ContentView(nint window) =>
        ObjC.msgSend(window, Sel("contentView"));

    internal static void AddSubview(nint parent, nint child) =>
        ObjC.msgSendVoid(parent, Sel("addSubview:"), child);

    internal static void SetHidden(nint view, bool hidden) =>
        ObjC.msgSendVoid(view, Sel("setHidden:"), hidden ? 1 : 0);

    internal static void SetStringValue(nint field, string text)
    {
        var nsText = StringNew(text);
        ObjC.msgSendVoid(field, Sel("setStringValue:"), nsText);
        Release(nsText);
    }

    internal static string GetStringValue(nint field)
    {
        var nsStr = ObjC.msgSend(field, Sel("stringValue"));
        return StringRead(nsStr);
    }

    internal static void SetEnabled(nint control, bool enabled) =>
        ObjC.msgSendVoid(control, Sel("setEnabled:"), enabled ? 1 : 0);

    internal static bool GetCheckboxState(nint checkbox) =>
        ObjC.msgSendInt(checkbox, Sel("state")) != 0; // NSControlStateValueOn = 1

    internal static void SetDoubleValue(nint control, double value) =>
        ObjC.msgSendVoid(control, Sel("setDoubleValue:"), value);

    // Font helpers
    internal static nint SystemFont(double size) =>
        ObjC.msgSend(Class("NSFont"), Sel("systemFontOfSize:"), (nint)(long)size);

    internal static nint BoldSystemFont(double size) =>
        ObjC.msgSend(Class("NSFont"), Sel("boldSystemFontOfSize:"), (nint)(long)size);

    internal static void SetFont(nint control, nint font) =>
        ObjC.msgSendVoid(control, Sel("setFont:"), font);

    // NSView container
    internal static nint CreateView(NSRect frame) =>
        ObjC.msgSendRect(Alloc("NSView"), Sel("initWithFrame:"), frame);

    // NSBundle helpers
    internal static nint MainBundle() =>
        ObjC.msgSend(Class("NSBundle"), Sel("mainBundle"));

    internal static string? BundleResourcePath()
    {
        var bundle = MainBundle();
        if (bundle == 0) return null;
        var nsPath = ObjC.msgSend(bundle, Sel("resourcePath"));
        if (nsPath == 0) return null;
        var utf8 = ObjC.msgSend(nsPath, Sel("UTF8String"));
        return utf8 != 0 ? Marshal.PtrToStringUTF8(utf8) : null;
    }
}
