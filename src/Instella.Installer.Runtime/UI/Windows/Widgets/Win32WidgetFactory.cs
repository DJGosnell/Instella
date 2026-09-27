using System;
using System.Collections.Generic;
using Instella.Installer.Runtime.UI.Widgets;

namespace Instella.Installer.Runtime.UI.Windows.Widgets;

/// <summary>
/// Creates Win32 controls for a single <see cref="Widget"/> according
/// to its record kind and a pre-computed <see cref="Win32LayoutSlot"/>.
/// Allocates sequential Win32 control IDs as compound widgets need
/// them, and writes each widget's default value into the bound
/// <see cref="PageState"/> so predicates see consistent state on
/// page entry.
/// </summary>
/// <remarks>
/// <para>The factory pattern-matches on the closed widget record
/// hierarchy (see <see cref="Widgets"/>). Every kind maps to a small
/// set of Win32 primitives — <c>STATIC</c>, <c>EDIT</c>, <c>BUTTON</c>,
/// <c>COMBOBOX</c>, or <c>msctls_progress32</c> — styled to match the
/// existing hand-coded Win32 wizard (Segoe UI Variable fonts, Explorer
/// visual theme).</para>
/// <para>Each factory instance is scoped to a single page panel. The
/// shared control-ID counter makes sure no two HWNDs on the same
/// parent share an ID, which the OS uses to dispatch WM_COMMAND.</para>
/// </remarks>
internal sealed class Win32WidgetFactory
{
    private const uint STM_SETIMAGE = 0x0172;
    private const uint IMAGE_BITMAP = 0;

    private readonly nint _parentHwnd;
    private readonly nint _hInstance;
    private readonly nint _bodyFont;
    private readonly nint _headerFont;
    private int _nextControlId;

    internal Win32WidgetFactory(
        nint parentHwnd,
        nint hInstance,
        nint bodyFont,
        nint headerFont,
        int firstControlId = 1000)
    {
        _parentHwnd = parentHwnd;
        _hInstance = hInstance;
        _bodyFont = bodyFont;
        _headerFont = headerFont;
        _nextControlId = firstControlId;
    }

    /// <summary>The next control ID that will be allocated. Exposed for tests.</summary>
    internal int PeekNextControlId => _nextControlId;

    /// <summary>
    /// Materialize <paramref name="widget"/> inside <paramref name="slot"/>,
    /// writing the widget's default value (if any) into <paramref name="state"/>.
    /// Returns an instance whose HWNDs are children of the factory's parent.
    /// </summary>
    internal Win32WidgetInstance Create(Widget widget, Win32LayoutSlot slot, PageState state)
    {
        return widget switch
        {
            Heading h => CreateHeading(h, slot),
            Paragraph p => CreateParagraph(p, slot),
            ScrollableText s => CreateScrollableText(s, slot),
            BrandImage b => CreateBrandImage(b, slot),
            TextInput t => CreateTextInput(t, slot, state),
            CheckBox c => CreateCheckBox(c, slot, state),
            RadioGroup r => CreateRadioGroup(r, slot, state),
            Dropdown d => CreateDropdown(d, slot, state),
            FolderPicker f => CreatePickerRow(f, f.Label, f.Default, slot, state),
            FilePicker f => CreatePickerRow(f, f.Label, f.Default, slot, state),
            Progress pr => CreateProgress(pr, slot),
            StatusLine st => CreateStatusLine(st, slot),
            _ => throw new ArgumentOutOfRangeException(
                nameof(widget),
                $"Win32WidgetFactory does not know how to render {widget.GetType().Name}. Extend the factory or remove the widget from the page."),
        };
    }

    private int AllocateId() => _nextControlId++;

    private nint CreateChild(string className, string text, uint style, uint exStyle, int x, int y, int w, int h, int id, nint font)
    {
        var hwnd = Win32.CreateWindowExW(
            exStyle, className, text,
            WS.CHILD | WS.VISIBLE | style,
            x, y, w, h,
            _parentHwnd, (nint)id, _hInstance, 0);

        if (hwnd != 0 && font != 0)
            Win32.SendMessageW(hwnd, WM.SETFONT, (nuint)font, 1);

        // Match the Explorer visual theme used by the rest of the Win32 UI.
        if (hwnd != 0)
            _ = Win32.SetWindowTheme(hwnd, "Explorer", null);

        return hwnd;
    }

    private Win32WidgetInstance CreateHeading(Heading heading, Win32LayoutSlot slot)
    {
        var id = AllocateId();
        var hwnd = CreateChild("STATIC", heading.Text, WS.SS_LEFT, 0,
            slot.Left, slot.Top, slot.Width, slot.Height, id, _headerFont);

        return new Win32WidgetInstance(heading, id, hwnd, new[] { hwnd });
    }

    private Win32WidgetInstance CreateParagraph(Paragraph paragraph, Win32LayoutSlot slot)
    {
        var id = AllocateId();
        var hwnd = CreateChild("STATIC", paragraph.Text, WS.SS_LEFT, 0,
            slot.Left, slot.Top, slot.Width, slot.Height, id, _bodyFont);

        return new Win32WidgetInstance(paragraph, id, hwnd, new[] { hwnd });
    }

    private Win32WidgetInstance CreateScrollableText(ScrollableText text, Win32LayoutSlot slot)
    {
        var id = AllocateId();
        var hwnd = CreateChild(
            "EDIT",
            text.Text,
            WS.ES_MULTILINE | WS.ES_READONLY | WS.ES_AUTOVSCROLL | WS.VSCROLL | WS.BORDER,
            0,
            slot.Left, slot.Top, slot.Width, slot.Height, id, _bodyFont);

        return new Win32WidgetInstance(text, id, hwnd, new[] { hwnd });
    }

    private Win32WidgetInstance CreateBrandImage(BrandImage image, Win32LayoutSlot slot)
    {
        var id = AllocateId();
        var hwnd = CreateChild(
            "STATIC",
            string.Empty,
            WS.SS_BITMAP | WS.SS_CENTERIMAGE,
            0,
            slot.Left, slot.Top, slot.Width, slot.Height, id, _bodyFont);

        // Fit the slot (the STATIC only crops) and blend transparency onto the dialog colour.
        var bitmap = GdiPlusImageLoader.Load(image.Source, slot.Width, slot.Height, Win32Branding.DialogBackgroundArgb());
        if (bitmap is not null && hwnd != 0)
        {
            // Binding an HBITMAP to a SS_BITMAP static — STATIC owns the
            // display, we own the handle.
            Win32.SendMessageW(hwnd, STM_SETIMAGE, (nuint)IMAGE_BITMAP, bitmap.Handle);
        }

        return new Win32WidgetInstance(image, id, hwnd, new[] { hwnd }, bitmap: bitmap);
    }

    private Win32WidgetInstance CreateTextInput(TextInput input, Win32LayoutSlot slot, PageState state)
    {
        var (labelSlot, controlSlot) = SplitLabelAndControl(slot);

        var labelId = AllocateId();
        var labelHwnd = CreateChild("STATIC", input.Label, WS.SS_LEFT, 0,
            labelSlot.Left, labelSlot.Top, labelSlot.Width, labelSlot.Height, labelId, _bodyFont);

        var editId = AllocateId();
        var editHwnd = CreateChild(
            "EDIT",
            input.Default,
            WS.ES_AUTOHSCROLL | WS.BORDER | WS.TABSTOP,
            0,
            controlSlot.Left, controlSlot.Top, controlSlot.Width, controlSlot.Height, editId, _bodyFont);

        if (!string.IsNullOrEmpty(input.Id))
            state.Set(input.Id, input.Default);

        return new Win32WidgetInstance(input, editId, editHwnd, new[] { labelHwnd, editHwnd });
    }

    private Win32WidgetInstance CreateCheckBox(CheckBox check, Win32LayoutSlot slot, PageState state)
    {
        var id = AllocateId();
        var hwnd = CreateChild(
            "BUTTON",
            check.Label,
            WS.BS_AUTOCHECKBOX | WS.TABSTOP,
            0,
            slot.Left, slot.Top, slot.Width, slot.Height, id, _bodyFont);

        if (check.Default && hwnd != 0)
            Win32.SendMessageW(hwnd, BM.SETCHECK, (nuint)BST.CHECKED, 0);

        if (!string.IsNullOrEmpty(check.Id))
            state.Set(check.Id, check.Default);

        return new Win32WidgetInstance(check, id, hwnd, new[] { hwnd });
    }

    private Win32WidgetInstance CreateRadioGroup(RadioGroup group, Win32LayoutSlot slot, PageState state)
    {
        var labelId = AllocateId();
        var labelHeight = LabelRowHeightAtSlot(slot);
        var gap = LabelToControlGapAtSlot(slot);
        var labelHwnd = CreateChild("STATIC", group.Label, WS.SS_LEFT, 0,
            slot.Left, slot.Top, slot.Width, labelHeight, labelId, _bodyFont);

        var allHwnds = new List<nint>(group.Options.Count + 1) { labelHwnd };
        var bindings = new List<RadioOptionBinding>(group.Options.Count);

        var optionsTop = slot.Top + labelHeight + gap;
        var remaining = slot.Height - labelHeight - gap;
        var perOption = group.Options.Count > 0
            ? remaining / group.Options.Count
            : remaining;
        if (perOption <= 0) perOption = 1;

        for (int i = 0; i < group.Options.Count; i++)
        {
            var option = group.Options[i];
            var optionId = AllocateId();
            // WS_GROUP on the first radio anchors keyboard-arrow navigation;
            // WS_TABSTOP on the currently-selected one lets Tab move focus in.
            var groupStyle = i == 0 ? WS.GROUP | WS.TABSTOP : 0u;
            var hwnd = CreateChild(
                "BUTTON",
                option.Label,
                WS.BS_AUTORADIOBUTTON | groupStyle,
                0,
                slot.Left, optionsTop + i * perOption, slot.Width, perOption,
                optionId, _bodyFont);

            bindings.Add(new RadioOptionBinding(optionId, hwnd, option.Value));
            allHwnds.Add(hwnd);

            if (group.Default is { } defaultValue && string.Equals(option.Value, defaultValue, StringComparison.Ordinal))
            {
                Win32.SendMessageW(hwnd, BM.SETCHECK, (nuint)BST.CHECKED, 0);
            }
        }

        if (!string.IsNullOrEmpty(group.Id))
            state.Set(group.Id, group.Default);

        var primaryHwnd = bindings.Count > 0 ? bindings[0].Hwnd : labelHwnd;
        var primaryId = bindings.Count > 0 ? bindings[0].ControlId : labelId;
        return new Win32WidgetInstance(group, primaryId, primaryHwnd, allHwnds, radioOptions: bindings);
    }

    private Win32WidgetInstance CreateDropdown(Dropdown dropdown, Win32LayoutSlot slot, PageState state)
    {
        var (labelSlot, controlSlot) = SplitLabelAndControl(slot);

        var labelId = AllocateId();
        var labelHwnd = CreateChild("STATIC", dropdown.Label, WS.SS_LEFT, 0,
            labelSlot.Left, labelSlot.Top, labelSlot.Width, labelSlot.Height, labelId, _bodyFont);

        var comboId = AllocateId();
        // ComboBox height must accommodate drop-down pane; pass a generous
        // height. The OS shows only the edit row until the list drops.
        var comboHeight = controlSlot.Height * 6;
        var comboHwnd = CreateChild(
            "COMBOBOX",
            string.Empty,
            WS.CBS_DROPDOWNLIST | WS.CBS_HASSTRINGS | WS.VSCROLL | WS.TABSTOP,
            0,
            controlSlot.Left, controlSlot.Top, controlSlot.Width, comboHeight, comboId, _bodyFont);

        var defaultIndex = -1;
        for (int i = 0; i < dropdown.Options.Count; i++)
        {
            var option = dropdown.Options[i];
            var hGlobal = System.Runtime.InteropServices.Marshal.StringToHGlobalUni(option);
            try
            {
                Win32.SendMessageW(comboHwnd, CB.ADDSTRING, 0, hGlobal);
            }
            finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(hGlobal); }

            if (dropdown.Default is { } d && string.Equals(option, d, StringComparison.Ordinal))
                defaultIndex = i;
        }

        if (defaultIndex >= 0)
            Win32.SendMessageW(comboHwnd, CB.SETCURSEL, (nuint)defaultIndex, 0);

        if (!string.IsNullOrEmpty(dropdown.Id))
            state.Set(dropdown.Id, dropdown.Default);

        return new Win32WidgetInstance(dropdown, comboId, comboHwnd, new[] { labelHwnd, comboHwnd });
    }

    private Win32WidgetInstance CreatePickerRow(Widget widget, string label, string? defaultPath, Win32LayoutSlot slot, PageState state)
    {
        var (labelSlot, controlSlot) = SplitLabelAndControl(slot);

        var labelId = AllocateId();
        var labelHwnd = CreateChild("STATIC", label, WS.SS_LEFT, 0,
            labelSlot.Left, labelSlot.Top, labelSlot.Width, labelSlot.Height, labelId, _bodyFont);

        // Reserve ~20% of width for the Browse button, keep 8px gutter.
        var browseWidth = System.Math.Max(64, controlSlot.Width * 20 / 100);
        var gutter = 8;
        var editWidth = controlSlot.Width - browseWidth - gutter;
        if (editWidth < 0) { editWidth = controlSlot.Width; browseWidth = 0; }

        var editId = AllocateId();
        var editHwnd = CreateChild(
            "EDIT",
            defaultPath ?? string.Empty,
            WS.ES_AUTOHSCROLL | WS.BORDER | WS.TABSTOP,
            0,
            controlSlot.Left, controlSlot.Top, editWidth, controlSlot.Height, editId, _bodyFont);

        int? browseId = null;
        var hwnds = new List<nint>(3) { labelHwnd, editHwnd };

        if (browseWidth > 0)
        {
            var id = AllocateId();
            browseId = id;
            var browseHwnd = CreateChild(
                "BUTTON",
                "Browse...",
                WS.BS_PUSHBUTTON | WS.TABSTOP,
                0,
                controlSlot.Left + editWidth + gutter, controlSlot.Top, browseWidth, controlSlot.Height,
                id, _bodyFont);
            hwnds.Add(browseHwnd);
        }

        if (widget is FolderPicker fp && !string.IsNullOrEmpty(fp.Id))
            state.Set(fp.Id, defaultPath ?? string.Empty);
        else if (widget is FilePicker fi && !string.IsNullOrEmpty(fi.Id))
            state.Set(fi.Id, defaultPath ?? string.Empty);

        return new Win32WidgetInstance(widget, editId, editHwnd, hwnds, browseButtonControlId: browseId);
    }

    private Win32WidgetInstance CreateProgress(Progress progress, Win32LayoutSlot slot)
    {
        var id = AllocateId();
        var hwnd = Win32.CreateWindowExW(
            0, "msctls_progress32", string.Empty,
            WS.CHILD | WS.VISIBLE | PBS.SMOOTH,
            slot.Left, slot.Top, slot.Width, slot.Height,
            _parentHwnd, (nint)id, _hInstance, 0);

        if (hwnd != 0)
        {
            _ = Win32.SetWindowTheme(hwnd, "Explorer", null);
            Win32.SendMessageW(hwnd, PBM.SETRANGE32, 0, 1000);
        }

        return new Win32WidgetInstance(progress, id, hwnd, new[] { hwnd });
    }

    private Win32WidgetInstance CreateStatusLine(StatusLine status, Win32LayoutSlot slot)
    {
        var id = AllocateId();
        var hwnd = CreateChild("STATIC", string.Empty, WS.SS_LEFT, 0,
            slot.Left, slot.Top, slot.Width, slot.Height, id, _bodyFont);

        return new Win32WidgetInstance(status, id, hwnd, new[] { hwnd });
    }

    // Compound widgets split their slot 25%/75% vertically: label on top,
    // control below, plus a fixed gap between them. Keeps the sub-layout
    // aligned with the 96-DPI design constants without importing the
    // layout engine's private numbers.
    private static (Win32LayoutSlot label, Win32LayoutSlot control) SplitLabelAndControl(Win32LayoutSlot slot)
    {
        var labelHeight = LabelRowHeightAtSlot(slot);
        var gap = LabelToControlGapAtSlot(slot);
        var controlTop = slot.Top + labelHeight + gap;
        var controlHeight = System.Math.Max(1, slot.Height - labelHeight - gap);

        var labelSlot = new Win32LayoutSlot(slot.Widget, slot.Left, slot.Top, slot.Width, labelHeight);
        var controlSlot = new Win32LayoutSlot(slot.Widget, slot.Left, controlTop, slot.Width, controlHeight);
        return (labelSlot, controlSlot);
    }

    private static int LabelRowHeightAtSlot(Win32LayoutSlot slot)
    {
        // Split proportionally to the design constants: 20 px label out of
        // 48 px compound total at 96 DPI → 5/12 ≈ 42%.
        return System.Math.Max(1, slot.Height * 5 / 12);
    }

    private static int LabelToControlGapAtSlot(Win32LayoutSlot slot)
    {
        return System.Math.Max(1, slot.Height * 1 / 12);
    }
}
