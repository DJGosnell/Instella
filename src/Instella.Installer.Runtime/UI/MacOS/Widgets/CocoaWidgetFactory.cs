using System;
using System.Runtime.Versioning;
using Instella.Installer.Runtime.UI.Widgets;

namespace Instella.Installer.Runtime.UI.MacOS.Widgets;

/// <summary>
/// Creates AppKit NSViews for a single <see cref="Widget"/> record,
/// wires target/action callbacks via <see cref="CocoaEventPump"/>,
/// and writes the widget's default value into <see cref="PageState"/>.
/// The returned <see cref="CocoaWidgetInstance"/> holds the
/// <see cref="CocoaWidgetInstance.RootView"/> for the owning page
/// container to add as a subview.
/// </summary>
/// <remarks>
/// Coordinate frames are built in Cocoa's bottom-origin convention
/// from the layout engine's top-origin slots via
/// <see cref="CocoaLayoutEngine.FlipY"/>. The factory is scoped to a
/// single page; different pages use their own factory so control-id
/// namespaces don't collide.
/// </remarks>
[SupportedOSPlatform("macos")]
internal sealed class CocoaWidgetFactory
{
    private readonly nint _pageContentView;
    private readonly int _panelHeightPx;
    private readonly PageState _state;
    private readonly CocoaEventPump _pump;

    internal CocoaWidgetFactory(nint pageContentView, int panelHeightPx, PageState state, CocoaEventPump pump)
    {
        _pageContentView = pageContentView;
        _panelHeightPx = panelHeightPx;
        _state = state;
        _pump = pump;
    }

    /// <summary>
    /// Materialize <paramref name="widget"/> into its NSViews inside
    /// <paramref name="topSlot"/>'s rect (top-origin). The instance's
    /// <see cref="CocoaWidgetInstance.RootView"/> is not yet added to
    /// any superview — the page panel does that in packing order.
    /// </summary>
    internal CocoaWidgetInstance Create(Widget widget, CocoaLayoutSlot topSlot)
    {
        var instance = widget switch
        {
            Heading h => CreateHeading(h, topSlot),
            Paragraph p => CreateParagraph(p, topSlot),
            ScrollableText s => CreateScrollableText(s, topSlot),
            BrandImage b => CreateBrandImage(b, topSlot),
            TextInput t => CreateTextInput(t, topSlot),
            CheckBox c => CreateCheckBox(c, topSlot),
            RadioGroup r => CreateRadioGroup(r, topSlot),
            Dropdown d => CreateDropdown(d, topSlot),
            FolderPicker f => CreatePickerRow(f, f.Label, f.Default, f.Id, topSlot),
            FilePicker f => CreatePickerRow(f, f.Label, f.Default, f.Id, topSlot),
            Progress pr => CreateProgress(pr, topSlot),
            StatusLine st => CreateStatusLine(st, topSlot),
            _ => throw new ArgumentOutOfRangeException(
                nameof(widget),
                $"CocoaWidgetFactory does not know how to render {widget.GetType().Name}."),
        };
        return instance;
    }

    private NSRect SlotToRect(CocoaLayoutSlot slot)
    {
        return new NSRect(
            slot.Left,
            CocoaLayoutEngine.FlipY(slot.Y, slot.Height, _panelHeightPx),
            slot.Width,
            slot.Height);
    }

    private NSRect ChildRect(int left, int topFromSlot, int width, int height, CocoaLayoutSlot slot)
    {
        // Cocoa child frames are relative to their superview's bottom-left.
        // The container NSView occupies the slot; the child's y = container.height - (topFromSlot + height).
        return new NSRect(left, slot.Height - topFromSlot - height, width, height);
    }

    private CocoaWidgetInstance CreateHeading(Heading h, CocoaLayoutSlot slot)
    {
        var label = NS.CreateLabel(h.Text, SlotToRect(slot));
        NS.SetFont(label, NS.BoldSystemFont(20));
        return new CocoaWidgetInstance(h, label, label);
    }

    private CocoaWidgetInstance CreateParagraph(Paragraph p, CocoaLayoutSlot slot)
    {
        var label = NS.CreateLabel(p.Text, SlotToRect(slot));
        return new CocoaWidgetInstance(p, label, label);
    }

    private CocoaWidgetInstance CreateScrollableText(ScrollableText st, CocoaLayoutSlot slot)
    {
        var scroll = ObjC.msgSendRect(NS.Alloc("NSScrollView"), NS.Sel("initWithFrame:"), SlotToRect(slot));
        ObjC.msgSendVoid(scroll, NS.Sel("setHasVerticalScroller:"), 1);
        ObjC.msgSendVoid(scroll, NS.Sel("setBorderType:"), 2); // NSBezelBorder

        var textView = ObjC.msgSendRect(NS.Alloc("NSTextView"), NS.Sel("initWithFrame:"),
            new NSRect(0, 0, slot.Width, slot.Height));
        ObjC.msgSendVoid(textView, NS.Sel("setEditable:"), 0);
        ObjC.msgSendVoid(textView, NS.Sel("setSelectable:"), 1);

        var nsText = NS.StringNew(st.Text);
        ObjC.msgSendVoid(textView, NS.Sel("setString:"), nsText);
        NS.Release(nsText);

        ObjC.msgSendVoid(scroll, NS.Sel("setDocumentView:"), textView);
        return new CocoaWidgetInstance(st, textView, scroll);
    }

    private CocoaWidgetInstance CreateBrandImage(BrandImage b, CocoaLayoutSlot slot)
    {
        var image = NSImageLoader.Load(b.Source);
        var imageView = ObjC.msgSendRect(NS.Alloc("NSImageView"), NS.Sel("initWithFrame:"), SlotToRect(slot));
        if (image != 0)
        {
            ObjC.msgSendVoid(imageView, NS.Sel("setImage:"), image);
            // ImageView retains; we keep our own ref on the instance.
        }
        ObjC.msgSendVoid(imageView, NS.Sel("setImageScaling:"), 3); // NSImageScaleProportionallyUpOrDown
        return new CocoaWidgetInstance(b, imageView, imageView, image: image);
    }

    private CocoaWidgetInstance CreateTextInput(TextInput t, CocoaLayoutSlot slot)
    {
        var container = NS.CreateView(SlotToRect(slot));
        var labelRect = ChildRect(0, 0, slot.Width, 20, slot);
        var editRect = ChildRect(0, 24, slot.Width, 24, slot);

        var label = NS.CreateLabel(t.Label, labelRect);
        NS.AddSubview(container, label);

        var field = NS.CreateTextField(t.Default, editRect);
        NS.AddSubview(container, field);

        WireTextField(field);
        var instance = new CocoaWidgetInstance(t, field, container);
        _pump.RegisterControl(field, instance);

        if (!string.IsNullOrEmpty(t.Id))
            _state.Set(t.Id, t.Default);

        return instance;
    }

    private CocoaWidgetInstance CreateCheckBox(CheckBox c, CocoaLayoutSlot slot)
    {
        var button = NS.CreateCheckbox(c.Label, SlotToRect(slot), c.Default);

        WireActionButton(button, "onToggle:");
        var instance = new CocoaWidgetInstance(c, button, button);
        _pump.RegisterControl(button, instance);

        if (!string.IsNullOrEmpty(c.Id))
            _state.Set(c.Id, c.Default);

        return instance;
    }

    private CocoaWidgetInstance CreateRadioGroup(RadioGroup r, CocoaLayoutSlot slot)
    {
        var container = NS.CreateView(SlotToRect(slot));
        var labelRect = ChildRect(0, 0, slot.Width, 20, slot);
        var label = NS.CreateLabel(r.Label, labelRect);
        NS.AddSubview(container, label);

        var bindings = new CocoaRadioBinding[r.Options.Count];
        for (int i = 0; i < r.Options.Count; i++)
        {
            var option = r.Options[i];
            var yFromTop = 24 + i * 24;
            var radioRect = ChildRect(0, yFromTop, slot.Width, 22, slot);

            var radio = ObjC.msgSendRect(NS.Alloc("NSButton"), NS.Sel("initWithFrame:"), radioRect);
            var title = NS.StringNew(option.Label);
            ObjC.msgSendVoid(radio, NS.Sel("setTitle:"), title);
            NS.Release(title);
            ObjC.msgSendVoid(radio, NS.Sel("setButtonType:"), 4); // NSRadioButton
            if (r.Default is { } def && string.Equals(option.Value, def, StringComparison.Ordinal))
                ObjC.msgSendVoid(radio, NS.Sel("setState:"), 1);

            NS.AddSubview(container, radio);
            bindings[i] = new CocoaRadioBinding(radio, option.Value);
        }

        var primary = bindings.Length > 0 ? bindings[0].View : label;
        var instance = new CocoaWidgetInstance(r, primary, container, radioOptions: bindings);
        for (int i = 0; i < bindings.Length; i++)
        {
            WireActionButton(bindings[i].View, "onRadio:");
            _pump.RegisterControl(bindings[i].View, instance, radioOptionIndex: i);
        }

        if (!string.IsNullOrEmpty(r.Id))
            _state.Set(r.Id, r.Default);

        return instance;
    }

    private CocoaWidgetInstance CreateDropdown(Dropdown d, CocoaLayoutSlot slot)
    {
        var container = NS.CreateView(SlotToRect(slot));
        var labelRect = ChildRect(0, 0, slot.Width, 20, slot);
        var popupRect = ChildRect(0, 24, slot.Width, 24, slot);

        var label = NS.CreateLabel(d.Label, labelRect);
        NS.AddSubview(container, label);

        var popup = ObjC.msgSendRect(NS.Alloc("NSPopUpButton"), NS.Sel("initWithFrame:"), popupRect);
        var defaultIndex = -1;
        for (int i = 0; i < d.Options.Count; i++)
        {
            var optionStr = NS.StringNew(d.Options[i]);
            ObjC.msgSendVoid(popup, NS.Sel("addItemWithTitle:"), optionStr);
            NS.Release(optionStr);
            if (d.Default is { } def && string.Equals(d.Options[i], def, StringComparison.Ordinal))
                defaultIndex = i;
        }
        if (defaultIndex >= 0)
            ObjC.msgSendVoid(popup, NS.Sel("selectItemAtIndex:"), defaultIndex);

        NS.AddSubview(container, popup);
        WireActionButton(popup, "onPopup:");

        var instance = new CocoaWidgetInstance(d, popup, container);
        _pump.RegisterControl(popup, instance);

        if (!string.IsNullOrEmpty(d.Id))
            _state.Set(d.Id, d.Default);

        return instance;
    }

    private CocoaWidgetInstance CreatePickerRow(Widget widget, string labelText, string? defaultPath, string? stateId, CocoaLayoutSlot slot)
    {
        var container = NS.CreateView(SlotToRect(slot));
        var labelRect = ChildRect(0, 0, slot.Width, 20, slot);
        var rowTop = 24;
        var rowHeight = 26;
        var browseWidth = Math.Max(80, slot.Width * 20 / 100);
        var gutter = 8;
        var editWidth = Math.Max(1, slot.Width - browseWidth - gutter);

        var label = NS.CreateLabel(labelText, labelRect);
        NS.AddSubview(container, label);

        var editRect = ChildRect(0, rowTop, editWidth, rowHeight, slot);
        var field = NS.CreateTextField(defaultPath ?? string.Empty, editRect);
        NS.AddSubview(container, field);
        WireTextField(field);

        var browseRect = ChildRect(editWidth + gutter, rowTop, browseWidth, rowHeight, slot);
        var browse = NS.CreateButton("Browse…", browseRect);
        NS.AddSubview(container, browse);
        WireActionButton(browse, "onBrowse:");

        var instance = new CocoaWidgetInstance(widget, field, container, browseButtonView: browse);
        _pump.RegisterControl(field, instance);
        _pump.RegisterControl(browse, instance);

        if (!string.IsNullOrEmpty(stateId))
            _state.Set(stateId, defaultPath ?? string.Empty);

        return instance;
    }

    private CocoaWidgetInstance CreateProgress(Progress pr, CocoaLayoutSlot slot)
    {
        var bar = NS.CreateProgressBar(SlotToRect(slot));
        return new CocoaWidgetInstance(pr, bar, bar);
    }

    private CocoaWidgetInstance CreateStatusLine(StatusLine st, CocoaLayoutSlot slot)
    {
        var label = NS.CreateLabel(string.Empty, SlotToRect(slot));
        return new CocoaWidgetInstance(st, label, label);
    }

    private void WireActionButton(nint button, string selectorName)
    {
        var target = _pump.SharedDelegate();
        ObjC.msgSendVoid(button, NS.Sel("setTarget:"), target);
        ObjC.msgSendVoid(button, NS.Sel("setAction:"), NS.Sel(selectorName));
    }

    private void WireTextField(nint field)
    {
        var del = _pump.SharedDelegate();
        ObjC.msgSendVoid(field, NS.Sel("setDelegate:"), del);
    }
}
