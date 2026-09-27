using System.Collections.Generic;
using Instella.Installer.Runtime.UI.Widgets;

namespace Instella.Installer.Runtime.UI.Linux.Widgets;

/// <summary>
/// Layout constants and widget ordering for the GTK renderer. Unlike
/// Win32 where we compute pixel rects, GTK positions widgets via
/// <c>GtkBox</c> packing and its built-in sizing algorithm. The
/// "layout engine" here just pins the spacing numbers the factory
/// passes to <c>gtk_box_new</c> / <c>gtk_widget_set_margin_*</c> so
/// tests can assert them without importing factory internals.
/// </summary>
internal static class GtkLayoutEngine
{
    /// <summary>Outer margin (start/end/top/bottom) of the page box, in logical pixels.</summary>
    internal const int ContentPadding = 16;

    /// <summary>Vertical spacing between widgets in the page box.</summary>
    internal const int VerticalSpacing = 10;

    /// <summary>Horizontal spacing within compound widget rows (e.g. Entry + Browse button).</summary>
    internal const int HorizontalRowSpacing = 6;

    /// <summary>Gap between a widget's label row and its input control.</summary>
    internal const int LabelToControlSpacing = 4;

    /// <summary>
    /// Minimum height (in logical pixels) reserved for a
    /// <see cref="ScrollableText"/> block. Matches the Win32
    /// renderer's design constant so wizard layouts feel consistent
    /// across platforms.
    /// </summary>
    internal const int ScrollableTextMinHeight = 160;

    /// <summary>
    /// Produce one <see cref="GtkLayoutSlot"/> per widget in the
    /// order they should pack into the page's <c>GtkBox</c>. The
    /// slot's <see cref="GtkLayoutSlot.Index"/> is its stack position
    /// and <see cref="GtkLayoutSlot.IsCompound"/> is <c>true</c> for
    /// widgets whose concrete render uses a sub-container (label +
    /// input rows).
    /// </summary>
    /// <remarks>
    /// Hidden widgets are kept in the slot list (with
    /// <see cref="GtkLayoutSlot.Visible"/> <c>= false</c>) because
    /// GTK reflows automatically when
    /// <c>gtk_widget_set_visible(false)</c> is called at runtime —
    /// unlike Win32 we don't need to reserve space.
    /// </remarks>
    internal static IReadOnlyList<GtkLayoutSlot> DescribeLayout(IReadOnlyList<Widget> widgets, PageState state)
    {
        var slots = new List<GtkLayoutSlot>(widgets.Count);
        for (int i = 0; i < widgets.Count; i++)
        {
            var w = widgets[i];
            var visible = w.Visible is not { } isVisible
                || Runners.UserCode.Run(() => isVisible(state), $"widget '{w.Id}' Visible", null, onError: true);
            slots.Add(new GtkLayoutSlot(w, Index: i, IsCompound: IsCompound(w), Visible: visible));
        }
        return slots;
    }

    internal static bool IsCompound(Widget w) => w is TextInput or Dropdown or FolderPicker or FilePicker or RadioGroup;
}

/// <summary>
/// Per-widget layout description produced by
/// <see cref="GtkLayoutEngine.DescribeLayout"/>. GTK does its own
/// pixel-level sizing; this record is structural metadata, not
/// geometry.
/// </summary>
internal readonly record struct GtkLayoutSlot(Widget Widget, int Index, bool IsCompound, bool Visible);
