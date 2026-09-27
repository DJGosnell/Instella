using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Instella.Installer.Runtime.UI.Widgets;

namespace Instella.Installer.Runtime.UI.Linux.Widgets;

/// <summary>
/// Wires GTK signal connections on a <see cref="GtkWidgetInstance"/>
/// and routes the callbacks into <see cref="PageState"/> writes.
/// Unlike Win32 where a single <c>WM_COMMAND</c> path dispatches
/// every control, GTK uses per-widget signals (e.g. "toggled" on
/// <c>GtkCheckButton</c>, "changed" on <c>GtkEntry</c>), so the pump
/// connects one handler per signal and stashes the GCHandle chain on
/// the instance for disconnect on dispose.
/// </summary>
/// <remarks>
/// <para>The pump is not a dispatcher — every callback fires directly
/// from GTK's main loop. Tests exercise the state-write logic by
/// constructing <see cref="GtkSignalContext"/> records and invoking
/// the internal <c>Handle*</c> helpers, which mirror what the
/// unmanaged callbacks do.</para>
/// </remarks>
internal sealed class GtkEventPump
{
    private readonly PageState _state;

    /// <summary>
    /// Fires when a FolderPicker / FilePicker Browse button is
    /// clicked. Host wires this to the GTK file-chooser dialog.
    /// </summary>
    internal event Action<GtkWidgetInstance>? BrowseRequested;

    internal GtkEventPump(PageState state) => _state = state;

    /// <summary>
    /// Connect the appropriate GTK signals to <paramref name="instance"/>
    /// based on its widget kind. Records the resulting GCHandles and
    /// signal IDs on the instance for disconnect on dispose.
    /// </summary>
    [SupportedOSPlatform("linux")]
    internal unsafe void Wire(GtkWidgetInstance instance)
    {
        switch (instance.Widget)
        {
            case CheckBox:
                ConnectStateful(instance, instance.PrimaryWidget, "toggled", &ToggledCallback);
                break;

            case TextInput:
            case FolderPicker:
            case FilePicker:
                ConnectStateful(instance, instance.PrimaryWidget, "changed", &EntryChangedCallback);
                if (instance.BrowseButtonWidget != 0)
                    ConnectStateful(instance, instance.BrowseButtonWidget, "clicked", &BrowseClickedCallback);
                break;

            case Dropdown:
                ConnectStateful(instance, instance.PrimaryWidget, "changed", &ComboChangedCallback);
                break;

            case RadioGroup:
                for (int i = 0; i < instance.RadioOptions.Count; i++)
                {
                    var ctx = new GtkSignalContext
                    {
                        Instance = instance,
                        Pump = this,
                        RadioOptionIndex = i,
                    };
                    ConnectWithContext(instance, instance.RadioOptions[i].Widget, "toggled", &RadioToggledCallback, ctx);
                }
                break;

            default:
                // Heading / Paragraph / ScrollableText / BrandImage / Progress / StatusLine
                // have no state — no signal to wire.
                break;
        }
    }

    [SupportedOSPlatform("linux")]
    private unsafe void ConnectStateful(
        GtkWidgetInstance instance, nint widget, string signal,
        delegate* unmanaged[Cdecl]<nint, nint, void> handler)
    {
        var ctx = new GtkSignalContext { Instance = instance, Pump = this };
        ConnectWithContext(instance, widget, signal, handler, ctx);
    }

    [SupportedOSPlatform("linux")]
    private unsafe void ConnectWithContext(
        GtkWidgetInstance instance, nint widget, string signal,
        delegate* unmanaged[Cdecl]<nint, nint, void> handler,
        GtkSignalContext context)
    {
        var handle = GCHandle.Alloc(context);
        instance.SignalHandles.Add(handle);
        var id = Gtk.g_signal_connect_data(widget, signal, (nint)handler, GCHandle.ToIntPtr(handle), 0, 0);
        instance.SignalConnections.Add((widget, id));
    }

    /// <summary>
    /// Write a checkbox's current state to <see cref="PageState"/>.
    /// Exposed for tests that simulate a "toggled" firing without a
    /// live GTK widget.
    /// </summary>
    internal void WriteCheckBox(GtkWidgetInstance instance, bool isActive)
    {
        if (instance.Widget is not CheckBox cb || string.IsNullOrEmpty(cb.Id)) return;
        _state.Set(cb.Id, isActive);
    }

    /// <summary>Write an entry's text to <see cref="PageState"/>. Exposed for tests.</summary>
    internal void WriteEntryText(GtkWidgetInstance instance, string text)
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

    /// <summary>Write a dropdown's selection to <see cref="PageState"/>. Exposed for tests.</summary>
    internal void WriteDropdownSelection(GtkWidgetInstance instance, int selectedIndex)
    {
        if (instance.Widget is not Dropdown dd || string.IsNullOrEmpty(dd.Id)) return;
        if (selectedIndex < 0 || selectedIndex >= dd.Options.Count)
        {
            _state.Set(dd.Id, null);
            return;
        }
        _state.Set(dd.Id, dd.Options[selectedIndex]);
    }

    /// <summary>
    /// Write a radio option's value to <see cref="PageState"/>.
    /// Called when the option becomes active. Exposed for tests.
    /// </summary>
    internal void WriteRadioSelection(GtkWidgetInstance instance, int optionIndex)
    {
        if (instance.Widget is not RadioGroup group || string.IsNullOrEmpty(group.Id)) return;
        if (optionIndex < 0 || optionIndex >= instance.RadioOptions.Count) return;
        _state.Set(group.Id, instance.RadioOptions[optionIndex].Value);
    }

    internal void RaiseBrowseRequested(GtkWidgetInstance instance) => BrowseRequested?.Invoke(instance);

    // Static unmanaged callbacks. Every GTK signal fires on the UI thread;
    // we recover the managed context from the GCHandle and dispatch.

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    [SupportedOSPlatform("linux")]
    private static void ToggledCallback(nint widget, nint data)
    {
        var ctx = (GtkSignalContext?)GCHandle.FromIntPtr(data).Target;
        if (ctx is null) return;
        var isActive = Gtk.gtk_toggle_button_get_active(widget) != 0;
        ctx.Pump.WriteCheckBox(ctx.Instance, isActive);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    [SupportedOSPlatform("linux")]
    private static void EntryChangedCallback(nint widget, nint data)
    {
        var ctx = (GtkSignalContext?)GCHandle.FromIntPtr(data).Target;
        if (ctx is null) return;
        var textPtr = Gtk.gtk_entry_get_text(widget);
        var text = textPtr == 0 ? string.Empty : (Marshal.PtrToStringUTF8(textPtr) ?? string.Empty);
        ctx.Pump.WriteEntryText(ctx.Instance, text);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    [SupportedOSPlatform("linux")]
    private static void ComboChangedCallback(nint widget, nint data)
    {
        var ctx = (GtkSignalContext?)GCHandle.FromIntPtr(data).Target;
        if (ctx is null) return;
        var index = Gtk.gtk_combo_box_get_active(widget);
        ctx.Pump.WriteDropdownSelection(ctx.Instance, index);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    [SupportedOSPlatform("linux")]
    private static void RadioToggledCallback(nint widget, nint data)
    {
        var ctx = (GtkSignalContext?)GCHandle.FromIntPtr(data).Target;
        if (ctx is null || ctx.RadioOptionIndex < 0) return;
        // Only the newly-active radio fires "toggled" twice (once for the
        // old one deactivating, once for the new one activating). We only
        // write state for the activating one.
        if (Gtk.gtk_toggle_button_get_active(widget) == 0) return;
        ctx.Pump.WriteRadioSelection(ctx.Instance, ctx.RadioOptionIndex);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    [SupportedOSPlatform("linux")]
    private static void BrowseClickedCallback(nint widget, nint data)
    {
        var ctx = (GtkSignalContext?)GCHandle.FromIntPtr(data).Target;
        if (ctx is null) return;
        ctx.Pump.RaiseBrowseRequested(ctx.Instance);
    }
}

/// <summary>
/// Context object stored in a GCHandle and passed to each GTK signal
/// callback so the static <c>[UnmanagedCallersOnly]</c> entry points
/// can reach back into managed state.
/// </summary>
internal sealed class GtkSignalContext
{
    internal GtkWidgetInstance Instance { get; init; } = null!;
    internal GtkEventPump Pump { get; init; } = null!;
    /// <summary>For RadioGroup bindings — which option index this callback represents.</summary>
    internal int RadioOptionIndex { get; init; } = -1;
}
