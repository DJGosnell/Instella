using System.Collections.Generic;
using Instella.Installer.Runtime.UI.Widgets;

namespace Instella.Installer.Runtime.UI.MacOS.Widgets;

/// <summary>
/// Deterministic vertical-stack layout engine for a Cocoa page. Like
/// Win32 this produces explicit frames; unlike Win32 Cocoa uses a
/// bottom-left origin coordinate system, so the engine emits slots
/// with <c>Y</c> measured from the page's top for intuitive authoring
/// and the factory flips to Cocoa coordinates at frame-creation time.
/// </summary>
/// <remarks>
/// <para>Heights per kind mirror the Win32 design constants so the
/// three platforms render at comparable proportions. A widget whose
/// <see cref="Widget.Visible"/> predicate returns <c>false</c> is
/// included in <see cref="ComputeIgnoringVisible"/> (used by the
/// panel to reserve a slot for every widget) but filtered out of
/// <see cref="Compute"/> (used by tests to verify runtime layout).</para>
/// </remarks>
internal static class CocoaLayoutEngine
{
    private const int ContentPadding = 16;
    private const int VerticalGap = 10;
    private const int LabelRowHeight = 20;
    private const int LabelToControlGap = 4;
    private const int EditHeight = 24;
    private const int ButtonHeight = 26;
    private const int HeadingHeight = 40;
    private const int ParagraphLineHeight = 20;
    private const int ScrollableTextHeight = 160;
    private const int CheckBoxHeight = 22;
    private const int RadioOptionHeight = 22;
    private const int RadioOptionGap = 2;
    private const int ProgressHeight = 22;
    private const int StatusLineHeight = 20;
    private const int ApproxCharsPerLine = 60;

    /// <summary>Layout filtered to visible widgets only.</summary>
    internal static IReadOnlyList<CocoaLayoutSlot> Compute(
        IReadOnlyList<Widget> widgets, PageState state, int pageWidthPx)
        => ComputeCore(widgets, state, pageWidthPx, respectVisible: true);

    /// <summary>
    /// Layout including every widget regardless of
    /// <see cref="Widget.Visible"/>. The panel uses this to reserve a
    /// slot for every widget so visibility toggles at runtime are
    /// pure <c>setHidden:</c> calls.
    /// </summary>
    internal static IReadOnlyList<CocoaLayoutSlot> ComputeIgnoringVisible(
        IReadOnlyList<Widget> widgets, int pageWidthPx)
        => ComputeCore(widgets, new PageState(), pageWidthPx, respectVisible: false);

    private static IReadOnlyList<CocoaLayoutSlot> ComputeCore(
        IReadOnlyList<Widget> widgets, PageState state, int pageWidthPx, bool respectVisible)
    {
        var contentWidth = pageWidthPx - ContentPadding * 2;
        if (contentWidth < 0) contentWidth = 0;

        var results = new List<CocoaLayoutSlot>(widgets.Count);
        var y = ContentPadding;
        var first = true;

        foreach (var widget in widgets)
        {
            if (respectVisible && widget.Visible is { } visiblePred
                && !Runners.UserCode.Run(() => visiblePred(state), $"widget '{widget.Id}' Visible", null, onError: true)) continue;

            var height = ComputeHeight(widget, contentWidth);
            if (!first) y += VerticalGap;
            first = false;

            results.Add(new CocoaLayoutSlot(widget, ContentPadding, y, contentWidth, height));
            y += height;
        }

        return results;
    }

    internal static int ComputeTotalHeight(IReadOnlyList<Widget> widgets, PageState state, int contentWidthPx)
    {
        var height = ContentPadding;
        var first = true;
        foreach (var widget in widgets)
        {
            if (widget.Visible is { } visiblePred
                && !Runners.UserCode.Run(() => visiblePred(state), $"widget '{widget.Id}' Visible", null, onError: true)) continue;
            if (!first) height += VerticalGap;
            first = false;
            height += ComputeHeight(widget, contentWidthPx);
        }
        height += ContentPadding;
        return height;
    }

    /// <summary>
    /// Convert a top-origin Y coordinate (what the layout engine
    /// emits) to Cocoa's bottom-origin Y for a panel of
    /// <paramref name="panelHeightPx"/>. The factory invokes this
    /// when building NSRect frames.
    /// </summary>
    internal static int FlipY(int topOriginY, int slotHeight, int panelHeightPx)
        => panelHeightPx - topOriginY - slotHeight;

    private static int ComputeHeight(Widget widget, int contentWidthPx) => widget switch
    {
        Heading => HeadingHeight,
        Paragraph p => ParagraphLineHeight * EstimateWrappedLines(p.Text, contentWidthPx),
        ScrollableText => ScrollableTextHeight,
        BrandImage img => img.MaxHeight,
        TextInput => LabelRowHeight + LabelToControlGap + EditHeight,
        CheckBox => CheckBoxHeight,
        RadioGroup rg => LabelRowHeight + LabelToControlGap
            + RadioOptionHeight * rg.Options.Count
            + RadioOptionGap * System.Math.Max(0, rg.Options.Count - 1),
        Dropdown => LabelRowHeight + LabelToControlGap + EditHeight,
        FolderPicker => LabelRowHeight + LabelToControlGap + System.Math.Max(EditHeight, ButtonHeight),
        FilePicker => LabelRowHeight + LabelToControlGap + System.Math.Max(EditHeight, ButtonHeight),
        Progress => ProgressHeight,
        StatusLine => StatusLineHeight,
        _ => LabelRowHeight,
    };

    private static int EstimateWrappedLines(string text, int contentWidthPx)
    {
        if (string.IsNullOrEmpty(text)) return 1;
        var charsPerLine = System.Math.Max(20, contentWidthPx * ApproxCharsPerLine / 360);
        var lines = (text.Length + charsPerLine - 1) / charsPerLine;
        return System.Math.Max(1, lines);
    }
}

/// <summary>
/// Per-widget slot produced by <see cref="CocoaLayoutEngine.Compute"/>.
/// <see cref="Y"/> is measured from the top of the page; the factory
/// flips this to Cocoa's bottom-origin when building NSRect frames.
/// </summary>
internal readonly record struct CocoaLayoutSlot(Widget Widget, int Left, int Y, int Width, int Height);
