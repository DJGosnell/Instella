using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Instella.Installer.Runtime.UI.Widgets;

namespace Instella.Installer.Runtime.UI.MacOS.Widgets;

/// <summary>
/// Routes AppKit target/action invocations and NSControl text-change
/// notifications into <see cref="PageState"/> writes. The pump owns
/// a single <c>InstellaCocoaWidgetDelegate</c> ObjC class (lazily
/// registered) and hands one instance to every widget as its
/// target/delegate; the action methods dispatch by looking up the
/// sender's NSView pointer in a static context map.
/// </summary>
/// <remarks>
/// <para>Pure-managed helpers — <c>WriteCheckBox</c>,
/// <c>WriteEditText</c>, <c>WriteDropdownSelection</c>,
/// <c>WriteRadioSelection</c> — are exposed <c>internal</c> so tests
/// on non-macOS hosts can exercise the state-write paths without
/// calling into AppKit.</para>
/// </remarks>
internal sealed class CocoaEventPump
{
    private static nint s_delegateClass;
    private static readonly object s_classGate = new();
    private static readonly ConcurrentDictionary<nint, CocoaSignalContext> s_contextByControl = new();

    private readonly PageState _state;
    private nint _sharedDelegateInstance;

    /// <summary>
    /// Fires when a FolderPicker / FilePicker Browse button is
    /// clicked. The host wires this to <c>NSOpenPanel</c>.
    /// </summary>
    internal event Action<CocoaWidgetInstance>? BrowseRequested;

    internal CocoaEventPump(PageState state) => _state = state;

    /// <summary>
    /// Return the pump's single <c>InstellaCocoaWidgetDelegate</c>
    /// instance — created on first access, retained for the lifetime
    /// of the pump. Every widget uses the same instance as its
    /// <c>target:</c> (buttons) or <c>delegate:</c> (text fields);
    /// dispatch happens via the static <see cref="s_contextByControl"/>
    /// lookup using sender's NSView pointer.
    /// </summary>
    [SupportedOSPlatform("macos")]
    internal nint SharedDelegate()
    {
        if (_sharedDelegateInstance != 0) return _sharedDelegateInstance;
        EnsureClassRegistered();
        _sharedDelegateInstance = ObjC.msgSend(ObjC.msgSend(s_delegateClass, NS.Sel("alloc")), NS.Sel("init"));
        return _sharedDelegateInstance;
    }

    /// <summary>Release the shared delegate instance. Idempotent.</summary>
    internal void ReleaseSharedDelegate()
    {
        if (_sharedDelegateInstance == 0) return;
        try { NS.Release(_sharedDelegateInstance); }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        _sharedDelegateInstance = 0;
    }

    /// <summary>
    /// Register <paramref name="control"/> so the static action
    /// callbacks can find its <see cref="CocoaWidgetInstance"/>.
    /// Called by the factory after each widget is created.
    /// </summary>
    internal void RegisterControl(nint control, CocoaWidgetInstance instance, int radioOptionIndex = -1)
    {
        if (control == 0) return;
        s_contextByControl[control] = new CocoaSignalContext
        {
            Instance = instance,
            Pump = this,
            RadioOptionIndex = radioOptionIndex,
        };
    }

    /// <summary>Unregister a control's binding on dispose.</summary>
    internal void UnregisterControl(nint control)
    {
        if (control != 0) s_contextByControl.TryRemove(control, out _);
    }

    /// <summary>Write checkbox state to <see cref="PageState"/>. Exposed for tests.</summary>
    internal void WriteCheckBox(CocoaWidgetInstance instance, bool isChecked)
    {
        if (instance.Widget is not CheckBox cb || string.IsNullOrEmpty(cb.Id)) return;
        _state.Set(cb.Id, isChecked);
    }

    /// <summary>Write text-field text to <see cref="PageState"/>. Exposed for tests.</summary>
    internal void WriteEditText(CocoaWidgetInstance instance, string text)
    {
        var id = instance.Widget switch
        {
            TextInput t => t.Id,
            FolderPicker f => f.Id,
            FilePicker f => f.Id,
            _ => null,
        };
        if (string.IsNullOrEmpty(id)) return;
        _state.Set(id, text);
    }

    /// <summary>Write popup-button selection to <see cref="PageState"/>. Exposed for tests.</summary>
    internal void WriteDropdownSelection(CocoaWidgetInstance instance, int selectedIndex)
    {
        if (instance.Widget is not Dropdown dd || string.IsNullOrEmpty(dd.Id)) return;
        if (selectedIndex < 0 || selectedIndex >= dd.Options.Count)
        {
            _state.Set(dd.Id, null);
            return;
        }
        _state.Set(dd.Id, dd.Options[selectedIndex]);
    }

    /// <summary>Write a radio option's value to <see cref="PageState"/>. Exposed for tests.</summary>
    internal void WriteRadioSelection(CocoaWidgetInstance instance, int optionIndex)
    {
        if (instance.Widget is not RadioGroup group || string.IsNullOrEmpty(group.Id)) return;
        if (optionIndex < 0 || optionIndex >= instance.RadioOptions.Count) return;
        _state.Set(group.Id, instance.RadioOptions[optionIndex].Value);
    }

    internal void RaiseBrowseRequested(CocoaWidgetInstance instance) => BrowseRequested?.Invoke(instance);

    [SupportedOSPlatform("macos")]
    private static unsafe void EnsureClassRegistered()
    {
        lock (s_classGate)
        {
            if (s_delegateClass != 0) return;

            s_delegateClass = ObjC.objc_allocateClassPair(NS.Class("NSObject"), "InstellaCocoaWidgetDelegate", 0);

            ObjC.class_addMethod(s_delegateClass, NS.Sel("onToggle:"),
                (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&OnToggleAction, "v@:@");
            ObjC.class_addMethod(s_delegateClass, NS.Sel("onPopup:"),
                (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&OnPopupAction, "v@:@");
            ObjC.class_addMethod(s_delegateClass, NS.Sel("onRadio:"),
                (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&OnRadioAction, "v@:@");
            ObjC.class_addMethod(s_delegateClass, NS.Sel("onBrowse:"),
                (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&OnBrowseAction, "v@:@");
            ObjC.class_addMethod(s_delegateClass, NS.Sel("controlTextDidChange:"),
                (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&OnControlTextDidChange, "v@:@");

            ObjC.objc_registerClassPair(s_delegateClass);
        }
    }

    // Static action callbacks. Each looks up the sender's pointer in
    // s_contextByControl and dispatches to the managed pump.

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    [SupportedOSPlatform("macos")]
    private static void OnToggleAction(nint self, nint sel, nint sender)
    {
        if (!s_contextByControl.TryGetValue(sender, out var ctx)) return;
        var isChecked = NS.GetCheckboxState(sender);
        ctx.Pump.WriteCheckBox(ctx.Instance, isChecked);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    [SupportedOSPlatform("macos")]
    private static void OnPopupAction(nint self, nint sel, nint sender)
    {
        if (!s_contextByControl.TryGetValue(sender, out var ctx)) return;
        var index = ObjC.msgSendInt(sender, NS.Sel("indexOfSelectedItem"));
        ctx.Pump.WriteDropdownSelection(ctx.Instance, index);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    [SupportedOSPlatform("macos")]
    private static void OnRadioAction(nint self, nint sel, nint sender)
    {
        if (!s_contextByControl.TryGetValue(sender, out var ctx)) return;
        if (ctx.RadioOptionIndex < 0) return;
        ctx.Pump.WriteRadioSelection(ctx.Instance, ctx.RadioOptionIndex);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    [SupportedOSPlatform("macos")]
    private static void OnBrowseAction(nint self, nint sel, nint sender)
    {
        if (!s_contextByControl.TryGetValue(sender, out var ctx)) return;
        ctx.Pump.RaiseBrowseRequested(ctx.Instance);
    }

    // controlTextDidChange: receives NSNotification; the control is
    // notification.object. We extract it and dispatch.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    [SupportedOSPlatform("macos")]
    private static void OnControlTextDidChange(nint self, nint sel, nint notification)
    {
        if (notification == 0) return;
        var control = ObjC.msgSend(notification, NS.Sel("object"));
        if (control == 0) return;
        if (!s_contextByControl.TryGetValue(control, out var ctx)) return;
        var text = NS.GetStringValue(control);
        ctx.Pump.WriteEditText(ctx.Instance, text);
    }
}

/// <summary>
/// Managed context bound to an AppKit control pointer so the static
/// ObjC delegate methods can dispatch back into the owning
/// <see cref="CocoaWidgetInstance"/>.
/// </summary>
internal sealed class CocoaSignalContext
{
    internal CocoaWidgetInstance Instance { get; init; } = null!;
    internal CocoaEventPump Pump { get; init; } = null!;
    internal int RadioOptionIndex { get; init; } = -1;
}
