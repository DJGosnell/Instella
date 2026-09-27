using System.Collections.Generic;
using Instella.Installer.Runtime.UI.Widgets;

namespace Instella.Installer.Runtime.UI.Windows.Widgets;

/// <summary>
/// Deterministic vertical-stack layout engine for a page of widgets.
/// Produces one <see cref="Win32LayoutSlot"/> per visible widget using
/// fixed 96-DPI design heights per widget kind, scaled to the target
/// DPI. No font-metric measurement, no reflow — the goal is that a
/// widget's rect depends only on its type and data shape, so tests and
/// renderers see identical geometry.
/// </summary>
/// <remarks>
/// <para>Widgets whose <see cref="Widget.Visible"/> predicate returns
/// <c>false</c> are excluded from the layout. They do not reserve
/// space; subsequent widgets shift up by the eliminated slot's height
/// plus the trailing gap.</para>
/// <para>Variable-height widgets (<see cref="Paragraph"/>,
/// <see cref="RadioGroup"/>) compute their height from data — text
/// length for paragraphs, option count for radio groups — using
/// conservative character-per-line estimates at the design DPI.
/// Renderers that need a different assumption must extend this class
/// rather than doing ad-hoc geometry, so layout tests stay authoritative.</para>
/// </remarks>
internal static class Win32LayoutEngine
{
    // 96-DPI design constants. All other dimensions scale from these.
    private const int ContentPaddingDesign = 16;
    private const int VerticalGapDesign = 10;
    private const int LabelRowHeightDesign = 20;
    private const int LabelToControlGapDesign = 4;
    private const int EditHeightDesign = 24;
    private const int ButtonHeightDesign = 26;
    private const int HeadingHeightDesign = 40;
    private const int ParagraphLineHeightDesign = 20;
    private const int ScrollableTextHeightDesign = 160;
    private const int CheckBoxHeightDesign = 22;
    private const int RadioOptionHeightDesign = 22;
    private const int RadioOptionGapDesign = 2;
    private const int ProgressHeightDesign = 22;
    private const int StatusLineHeightDesign = 20;
    private const int ApproxCharsPerLineAt60Px = 60;

    /// <summary>
    /// Compute layout slots for <paramref name="widgets"/> within a page
    /// whose client-area width is <paramref name="pageWidthPx"/> at the
    /// given <paramref name="dpi"/>. Widgets whose
    /// <see cref="Widget.Visible"/> predicate returns <c>false</c> are
    /// omitted from the output.
    /// </summary>
    internal static IReadOnlyList<Win32LayoutSlot> Compute(
        IReadOnlyList<Widget> widgets,
        PageState state,
        int pageWidthPx,
        int dpi,
        int? pageHeightPx = null)
        => ComputeCore(widgets, state, pageWidthPx, dpi, respectVisible: true, pageHeightPx);

    /// <summary>
    /// Compute layout slots for every widget regardless of
    /// <see cref="Widget.Visible"/>. <see cref="Win32PagePanel"/> uses
    /// this to allocate a slot for every widget so that toggling
    /// visibility at runtime is a pure <c>ShowWindow</c> call — no
    /// layout changes, no control recreation.
    /// </summary>
    internal static IReadOnlyList<Win32LayoutSlot> ComputeIgnoringVisible(
        IReadOnlyList<Widget> widgets,
        int pageWidthPx,
        int dpi,
        int? pageHeightPx = null)
        => ComputeCore(widgets, state: new PageState(), pageWidthPx, dpi, respectVisible: false, pageHeightPx);

    /// <summary>
    /// Stacks the widgets. With <c>pageHeightPx</c> (the page's height, when known) the space
    /// left below the stack goes to the <see cref="ScrollableText"/> widgets, so a licence or an
    /// error fills the page instead of a fixed box, and the widgets below them move down.
    /// </summary>
    private static IReadOnlyList<Win32LayoutSlot> ComputeCore(
        IReadOnlyList<Widget> widgets,
        PageState state,
        int pageWidthPx,
        int dpi,
        bool respectVisible,
        int? pageHeightPx)
    {
        var padding = Win32DpiAware.Scale(ContentPaddingDesign, dpi);
        var gap = Win32DpiAware.Scale(VerticalGapDesign, dpi);
        var contentWidth = pageWidthPx - padding * 2;
        if (contentWidth < 0) contentWidth = 0;

        var placed = new List<(Widget Widget, int Height)>(widgets.Count);
        foreach (var widget in widgets)
        {
            if (respectVisible && widget.Visible is { } visiblePred
                && !Runners.UserCode.Run(() => visiblePred(state), $"widget '{widget.Id}' Visible", null, onError: true)) continue;
            placed.Add((widget, ComputeHeight(widget, contentWidth, dpi)));
        }

        if (pageHeightPx is { } pageHeight)
        {
            var used = padding * 2 + gap * System.Math.Max(0, placed.Count - 1);
            foreach (var p in placed) used += p.Height;
            var fillers = placed.FindAll(p => p.Widget is ScrollableText).Count;
            var spare = pageHeight - used;
            if (fillers > 0 && spare > 0)
            {
                for (var i = 0; i < placed.Count; i++)
                {
                    if (placed[i].Widget is not ScrollableText) continue;
                    var share = spare / fillers;
                    placed[i] = (placed[i].Widget, placed[i].Height + share);
                    spare -= share;
                    fillers--;
                }
            }
        }

        var results = new List<Win32LayoutSlot>(placed.Count);
        var y = padding;
        for (var i = 0; i < placed.Count; i++)
        {
            if (i > 0) y += gap;
            results.Add(new Win32LayoutSlot(placed[i].Widget, padding, y, contentWidth, placed[i].Height));
            y += placed[i].Height;
        }

        return results;
    }

    /// <summary>
    /// Compute the total vertical extent (including top + bottom padding
    /// + gaps) of a layout produced for <paramref name="widgets"/>.
    /// Useful for host windows that size themselves to content.
    /// </summary>
    internal static int ComputeTotalHeight(
        IReadOnlyList<Widget> widgets,
        PageState state,
        int contentWidthPx,
        int dpi)
    {
        var padding = Win32DpiAware.Scale(ContentPaddingDesign, dpi);
        var gap = Win32DpiAware.Scale(VerticalGapDesign, dpi);

        var height = padding;
        var first = true;
        foreach (var widget in widgets)
        {
            if (widget.Visible is { } visiblePred
                && !Runners.UserCode.Run(() => visiblePred(state), $"widget '{widget.Id}' Visible", null, onError: true)) continue;
            if (!first) height += gap;
            first = false;
            height += ComputeHeight(widget, contentWidthPx, dpi);
        }
        height += padding;
        return height;
    }

    /// <summary>
    /// Per-kind nominal height at the given DPI. Compound widgets sum
    /// their sub-control heights plus internal gaps; variable widgets
    /// compute from data.
    /// </summary>
    private static int ComputeHeight(Widget widget, int contentWidthPx, int dpi) => widget switch
    {
        Heading => Win32DpiAware.Scale(HeadingHeightDesign, dpi),

        Paragraph p => Win32DpiAware.Scale(
            ParagraphLineHeightDesign * EstimateWrappedLines(p.Text, contentWidthPx, dpi),
            dpi),

        ScrollableText => Win32DpiAware.Scale(ScrollableTextHeightDesign, dpi),

        BrandImage img => Win32DpiAware.Scale(img.MaxHeight, dpi),

        TextInput => Win32DpiAware.Scale(
            LabelRowHeightDesign + LabelToControlGapDesign + EditHeightDesign, dpi),

        CheckBox => Win32DpiAware.Scale(CheckBoxHeightDesign, dpi),

        RadioGroup rg => Win32DpiAware.Scale(
            LabelRowHeightDesign + LabelToControlGapDesign
            + RadioOptionHeightDesign * rg.Options.Count
            + RadioOptionGapDesign * System.Math.Max(0, rg.Options.Count - 1),
            dpi),

        Dropdown => Win32DpiAware.Scale(
            LabelRowHeightDesign + LabelToControlGapDesign + EditHeightDesign, dpi),

        FolderPicker => Win32DpiAware.Scale(
            LabelRowHeightDesign + LabelToControlGapDesign
            + System.Math.Max(EditHeightDesign, ButtonHeightDesign), dpi),

        FilePicker => Win32DpiAware.Scale(
            LabelRowHeightDesign + LabelToControlGapDesign
            + System.Math.Max(EditHeightDesign, ButtonHeightDesign), dpi),

        Progress => Win32DpiAware.Scale(ProgressHeightDesign, dpi),

        StatusLine => Win32DpiAware.Scale(StatusLineHeightDesign, dpi),

        _ => Win32DpiAware.Scale(LabelRowHeightDesign, dpi),
    };

    /// <summary>
    /// Estimate how many wrapped lines a paragraph of body text will
    /// occupy at the given content width. Uses a constant characters-
    /// per-line approximation at 96 DPI — that's close enough for
    /// Segoe UI body text at the sizes Instella ships. Minimum 1 line.
    /// </summary>
    private static int EstimateWrappedLines(string text, int contentWidthPx, int dpi)
    {
        if (string.IsNullOrEmpty(text)) return 1;

        var widthIn96Px = contentWidthPx * Win32DpiAware.BaseDpi / (dpi <= 0 ? Win32DpiAware.BaseDpi : dpi);
        var charsPerLine = System.Math.Max(20, widthIn96Px * ApproxCharsPerLineAt60Px / 360);
        var lines = (text.Length + charsPerLine - 1) / charsPerLine;
        return System.Math.Max(1, lines);
    }
}
