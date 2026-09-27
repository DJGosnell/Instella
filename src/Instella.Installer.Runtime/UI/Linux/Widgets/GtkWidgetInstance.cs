using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Instella.Installer.Runtime.UI.Widgets;

namespace Instella.Installer.Runtime.UI.Linux.Widgets;

/// <summary>
/// A materialized widget on a GTK page — the GtkWidget pointers that
/// implement a single <see cref="Widget"/> record, together with
/// bookkeeping for signal disconnect and pixbuf release on dispose.
/// </summary>
/// <remarks>
/// <para>GTK's reference model differs from Win32: <c>GtkContainer</c>
/// owns its children, so destroying the parent window cascades. But
/// pixbufs and signal contexts are refcounted / managed separately.
/// This type tracks only what we allocated and releases exactly
/// that.</para>
/// </remarks>
internal sealed class GtkWidgetInstance : IDisposable
{
    /// <summary>The source widget record.</summary>
    internal Widget Widget { get; }

    /// <summary>
    /// Primary GtkWidget pointer — the one whose state the event
    /// pump reads back into <see cref="PageState"/>.
    /// </summary>
    internal nint PrimaryWidget { get; }

    /// <summary>
    /// Root GtkWidget for packing into a container. For compound
    /// widgets this is the outer GtkBox; for simple widgets it's the
    /// same as <see cref="PrimaryWidget"/>.
    /// </summary>
    internal nint RootWidget { get; }

    /// <summary>
    /// Browse button pointer, if any (FolderPicker / FilePicker).
    /// Null for every other widget kind.
    /// </summary>
    internal nint BrowseButtonWidget { get; }

    /// <summary>One binding per radio option for a <see cref="RadioGroup"/>.</summary>
    internal IReadOnlyList<GtkRadioBinding> RadioOptions { get; }

    /// <summary>
    /// Refcount-owned pixbuf bound to this instance, if any. Freed on
    /// dispose.
    /// </summary>
    internal nint Pixbuf { get; private set; }

    /// <summary>GCHandles allocated for signal callbacks — freed on dispose.</summary>
    internal List<GCHandle> SignalHandles { get; } = new();

    /// <summary>Signal (widget, handlerId) pairs for disconnect on dispose.</summary>
    internal List<(nint widget, ulong handlerId)> SignalConnections { get; } = new();

    internal GtkWidgetInstance(
        Widget widget,
        nint primaryWidget,
        nint rootWidget,
        nint browseButtonWidget = 0,
        IReadOnlyList<GtkRadioBinding>? radioOptions = null,
        nint pixbuf = 0)
    {
        Widget = widget;
        PrimaryWidget = primaryWidget;
        RootWidget = rootWidget;
        BrowseButtonWidget = browseButtonWidget;
        RadioOptions = radioOptions ?? Array.Empty<GtkRadioBinding>();
        Pixbuf = pixbuf;
    }

    public void Dispose()
    {
        foreach (var (widget, handlerId) in SignalConnections)
        {
            try { Gtk.g_signal_handler_disconnect(widget, handlerId); }
            catch (DllNotFoundException) { /* non-Linux host */ }
            catch (EntryPointNotFoundException) { }
        }
        SignalConnections.Clear();

        foreach (var handle in SignalHandles)
        {
            if (handle.IsAllocated) handle.Free();
        }
        SignalHandles.Clear();

        if (Pixbuf != 0)
        {
            try { Gtk.g_object_unref(Pixbuf); }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            Pixbuf = 0;
        }

        // GtkWidgets are freed when their container is destroyed. We deliberately
        // don't call gtk_widget_destroy on every owned widget — that risks a
        // double-free if the panel destroys its container first.
    }
}

/// <summary>Binding of one <see cref="RadioOption"/> to its GTK radio-button pointer.</summary>
internal readonly record struct GtkRadioBinding(nint Widget, string Value);
