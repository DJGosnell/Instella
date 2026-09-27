using System;
using System.Collections.Generic;
using Instella.Installer.Runtime.UI.Widgets;

namespace Instella.Installer.Runtime.UI.MacOS.Widgets;

/// <summary>
/// A materialized widget on a Cocoa page — the NSView pointers that
/// implement a single <see cref="Widget"/> record, plus bookkeeping
/// for target/action delegate lifetime and NSImage release on dispose.
/// </summary>
/// <remarks>
/// Cocoa's reference model is manual (no ARC). Parent NSView
/// <c>release</c> does not cascade to subviews once removed; we
/// deliberately do NOT call <c>release</c> on owned NSViews because
/// they're retained by their superview. We DO release NSImage
/// instances loaded by <see cref="NSImageLoader"/> since the
/// NSImageView retains its own reference.
/// </remarks>
internal sealed class CocoaWidgetInstance : IDisposable
{
    /// <summary>The source widget record.</summary>
    internal Widget Widget { get; }

    /// <summary>
    /// Primary NSView pointer — the one whose state the event pump
    /// reads back into <see cref="PageState"/>.
    /// </summary>
    internal nint PrimaryView { get; }

    /// <summary>
    /// Root NSView for packing into the page container. For compound
    /// widgets this is an NSView holding the children; for simple
    /// widgets it's the same as <see cref="PrimaryView"/>.
    /// </summary>
    internal nint RootView { get; }

    /// <summary>Browse button NSView, if any (FolderPicker / FilePicker).</summary>
    internal nint BrowseButtonView { get; }

    /// <summary>One binding per radio option for a <see cref="RadioGroup"/>.</summary>
    internal IReadOnlyList<CocoaRadioBinding> RadioOptions { get; }

    /// <summary>NSImage pointer bound to this instance. Released on dispose.</summary>
    internal nint Image { get; private set; }

    /// <summary>
    /// Pointers to delegate objects (instances of the shared
    /// <c>InstellaWidgetDelegate</c> ObjC class) that must be
    /// released when the widget is torn down.
    /// </summary>
    internal List<nint> DelegateTargets { get; } = new();

    internal CocoaWidgetInstance(
        Widget widget,
        nint primaryView,
        nint rootView,
        nint browseButtonView = 0,
        IReadOnlyList<CocoaRadioBinding>? radioOptions = null,
        nint image = 0)
    {
        Widget = widget;
        PrimaryView = primaryView;
        RootView = rootView;
        BrowseButtonView = browseButtonView;
        RadioOptions = radioOptions ?? Array.Empty<CocoaRadioBinding>();
        Image = image;
    }

    public void Dispose()
    {
        foreach (var target in DelegateTargets)
        {
            if (target != 0)
            {
                try { NS.Release(target); }
                catch (DllNotFoundException) { /* non-macOS host */ }
                catch (EntryPointNotFoundException) { }
            }
        }
        DelegateTargets.Clear();

        if (Image != 0)
        {
            try { NS.Release(Image); }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            Image = 0;
        }

        // NSViews are retained by their superview; we don't release them here
        // to avoid double-free. The host window's closure cascades cleanup
        // via NSWindow release, which releases its content view, which
        // releases its subview tree.
    }
}

/// <summary>Binding of one <see cref="RadioOption"/> to its NSButton pointer.</summary>
internal readonly record struct CocoaRadioBinding(nint View, string Value);
