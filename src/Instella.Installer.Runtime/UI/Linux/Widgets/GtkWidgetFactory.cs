using System;
using System.Runtime.Versioning;
using Instella.Installer.Runtime.UI.Widgets;

namespace Instella.Installer.Runtime.UI.Linux.Widgets;

/// <summary>
/// Creates GTK widgets for a single <see cref="Widget"/> record and
/// writes each widget's default value into the bound
/// <see cref="PageState"/> so predicates see consistent state on page
/// entry. Wires the appropriate signals via <see cref="GtkEventPump"/>
/// so value-carrying widgets feed PageState automatically.
/// </summary>
/// <remarks>
/// <para>Compound widgets (label + input, label + radio stack,
/// picker row) return a root <c>GtkBox</c> that bundles the children.
/// Simple widgets return their primary GtkWidget as both root and
/// primary. Every widget's lifetime is tied to its eventual container
/// — <c>gtk_widget_destroy</c> on the page box cascades to
/// descendants.</para>
/// </remarks>
[SupportedOSPlatform("linux")]
internal sealed class GtkWidgetFactory
{
    private readonly PageState _state;
    private readonly GtkEventPump _pump;

    internal GtkWidgetFactory(PageState state, GtkEventPump pump)
    {
        _state = state;
        _pump = pump;
    }

    /// <summary>
    /// Materialize <paramref name="widget"/> as a GTK widget tree.
    /// The returned instance's <see cref="GtkWidgetInstance.RootWidget"/>
    /// should be packed into the owning page's container.
    /// </summary>
    internal GtkWidgetInstance Create(Widget widget)
    {
        var instance = widget switch
        {
            Heading h => CreateHeading(h),
            Paragraph p => CreateParagraph(p),
            ScrollableText s => CreateScrollableText(s),
            BrandImage b => CreateBrandImage(b),
            TextInput t => CreateTextInput(t),
            CheckBox c => CreateCheckBox(c),
            RadioGroup r => CreateRadioGroup(r),
            Dropdown d => CreateDropdown(d),
            FolderPicker f => CreatePickerRow(f, f.Label, f.Default, f.Id),
            FilePicker f => CreatePickerRow(f, f.Label, f.Default, f.Id),
            Progress pr => CreateProgress(pr),
            StatusLine st => CreateStatusLine(st),
            _ => throw new ArgumentOutOfRangeException(
                nameof(widget),
                $"GtkWidgetFactory does not know how to render {widget.GetType().Name}."),
        };

        _pump.Wire(instance);
        return instance;
    }

    private static GtkWidgetInstance CreateHeading(Heading h)
    {
        var label = Gtk.gtk_label_new(null);
        Gtk.gtk_label_set_markup(label, $"<span size='x-large' weight='bold'>{Escape(h.Text)}</span>");
        Gtk.gtk_label_set_xalign(label, 0);
        return new GtkWidgetInstance(h, label, label);
    }

    private static GtkWidgetInstance CreateParagraph(Paragraph p)
    {
        var label = Gtk.gtk_label_new(p.Text);
        Gtk.gtk_label_set_xalign(label, 0);
        // Enable word wrap via underlying GtkLabel "wrap" property — we
        // reach it via the markup channel since we don't bind
        // gtk_label_set_line_wrap directly. Paragraphs are short enough
        // that the default single-line render is acceptable; future work
        // can bind the wrap setter.
        return new GtkWidgetInstance(p, label, label);
    }

    private static GtkWidgetInstance CreateScrollableText(ScrollableText st)
    {
        var sw = Gtk.gtk_scrolled_window_new(0, 0);
        Gtk.gtk_scrolled_window_set_policy(sw, Gtk.GTK_POLICY_NEVER, Gtk.GTK_POLICY_AUTOMATIC);
        Gtk.gtk_widget_set_size_request(sw, -1, GtkLayoutEngine.ScrollableTextMinHeight);

        var textView = Gtk.gtk_text_view_new();
        Gtk.gtk_text_view_set_editable(textView, 0);
        Gtk.gtk_text_view_set_wrap_mode(textView, Gtk.GTK_WRAP_WORD_CHAR);

        var buffer = Gtk.gtk_text_view_get_buffer(textView);
        Gtk.gtk_text_buffer_set_text(buffer, st.Text, -1);

        Gtk.gtk_container_add(sw, textView);
        return new GtkWidgetInstance(st, textView, sw);
    }

    private static GtkWidgetInstance CreateBrandImage(BrandImage b)
    {
        var pixbuf = GdkPixbufLoader.Load(b.Source);
        var image = pixbuf == 0 ? Gtk.gtk_image_new() : Gtk.gtk_image_new_from_pixbuf(pixbuf);
        if (b.MaxHeight > 0)
            Gtk.gtk_widget_set_size_request(image, -1, b.MaxHeight);
        return new GtkWidgetInstance(b, image, image, pixbuf: pixbuf);
    }

    private GtkWidgetInstance CreateTextInput(TextInput t)
    {
        var outer = Gtk.gtk_box_new(Gtk.GTK_ORIENTATION_VERTICAL, GtkLayoutEngine.LabelToControlSpacing);

        var label = Gtk.gtk_label_new(t.Label);
        Gtk.gtk_label_set_xalign(label, 0);
        Gtk.gtk_box_pack_start(outer, label, 0, 0, 0);

        var entry = Gtk.gtk_entry_new();
        Gtk.gtk_entry_set_text(entry, t.Default);
        Gtk.gtk_widget_set_hexpand(entry, 1);
        Gtk.gtk_box_pack_start(outer, entry, 0, 0, 0);

        if (!string.IsNullOrEmpty(t.Id))
            _state.Set(t.Id, t.Default);

        return new GtkWidgetInstance(t, entry, outer);
    }

    private GtkWidgetInstance CreateCheckBox(CheckBox c)
    {
        var button = Gtk.gtk_check_button_new_with_label(c.Label);
        Gtk.gtk_toggle_button_set_active(button, c.Default ? 1 : 0);

        if (!string.IsNullOrEmpty(c.Id))
            _state.Set(c.Id, c.Default);

        return new GtkWidgetInstance(c, button, button);
    }

    private GtkWidgetInstance CreateRadioGroup(RadioGroup r)
    {
        var outer = Gtk.gtk_box_new(Gtk.GTK_ORIENTATION_VERTICAL, GtkLayoutEngine.LabelToControlSpacing);

        var label = Gtk.gtk_label_new(r.Label);
        Gtk.gtk_label_set_xalign(label, 0);
        Gtk.gtk_box_pack_start(outer, label, 0, 0, 0);

        var stack = Gtk.gtk_box_new(Gtk.GTK_ORIENTATION_VERTICAL, 2);
        Gtk.gtk_box_pack_start(outer, stack, 0, 0, 0);

        var bindings = new GtkRadioBinding[r.Options.Count];
        nint firstRadio = 0;
        for (int i = 0; i < r.Options.Count; i++)
        {
            var option = r.Options[i];
            var radio = i == 0
                ? Gtk.gtk_radio_button_new_with_label(0, option.Label)
                : Gtk.gtk_radio_button_new_with_label_from_widget(firstRadio, option.Label);

            if (i == 0) firstRadio = radio;
            Gtk.gtk_box_pack_start(stack, radio, 0, 0, 0);
            bindings[i] = new GtkRadioBinding(radio, option.Value);

            if (r.Default is { } defaultValue && string.Equals(option.Value, defaultValue, StringComparison.Ordinal))
                Gtk.gtk_toggle_button_set_active(radio, 1);
        }

        if (!string.IsNullOrEmpty(r.Id))
            _state.Set(r.Id, r.Default);

        var primary = bindings.Length > 0 ? bindings[0].Widget : label;
        return new GtkWidgetInstance(r, primary, outer, radioOptions: bindings);
    }

    private GtkWidgetInstance CreateDropdown(Dropdown d)
    {
        var outer = Gtk.gtk_box_new(Gtk.GTK_ORIENTATION_VERTICAL, GtkLayoutEngine.LabelToControlSpacing);

        var label = Gtk.gtk_label_new(d.Label);
        Gtk.gtk_label_set_xalign(label, 0);
        Gtk.gtk_box_pack_start(outer, label, 0, 0, 0);

        var combo = Gtk.gtk_combo_box_text_new();
        Gtk.gtk_box_pack_start(outer, combo, 0, 0, 0);

        var defaultIndex = -1;
        for (int i = 0; i < d.Options.Count; i++)
        {
            Gtk.gtk_combo_box_text_append_text(combo, d.Options[i]);
            if (d.Default is { } dd && string.Equals(d.Options[i], dd, StringComparison.Ordinal))
                defaultIndex = i;
        }
        if (defaultIndex >= 0) Gtk.gtk_combo_box_set_active(combo, defaultIndex);

        if (!string.IsNullOrEmpty(d.Id))
            _state.Set(d.Id, d.Default);

        return new GtkWidgetInstance(d, combo, outer);
    }

    private GtkWidgetInstance CreatePickerRow(Widget widget, string labelText, string? defaultPath, string? stateId)
    {
        var outer = Gtk.gtk_box_new(Gtk.GTK_ORIENTATION_VERTICAL, GtkLayoutEngine.LabelToControlSpacing);

        var label = Gtk.gtk_label_new(labelText);
        Gtk.gtk_label_set_xalign(label, 0);
        Gtk.gtk_box_pack_start(outer, label, 0, 0, 0);

        var row = Gtk.gtk_box_new(Gtk.GTK_ORIENTATION_HORIZONTAL, GtkLayoutEngine.HorizontalRowSpacing);
        Gtk.gtk_box_pack_start(outer, row, 0, 0, 0);

        var entry = Gtk.gtk_entry_new();
        Gtk.gtk_entry_set_text(entry, defaultPath ?? string.Empty);
        Gtk.gtk_widget_set_hexpand(entry, 1);
        Gtk.gtk_box_pack_start(row, entry, 1, 1, 0);

        var browse = Gtk.gtk_button_new_with_label("Browse…");
        Gtk.gtk_box_pack_start(row, browse, 0, 0, 0);

        if (!string.IsNullOrEmpty(stateId))
            _state.Set(stateId, defaultPath ?? string.Empty);

        return new GtkWidgetInstance(widget, entry, outer, browseButtonWidget: browse);
    }

    private static GtkWidgetInstance CreateProgress(Progress pr)
    {
        var bar = Gtk.gtk_progress_bar_new();
        return new GtkWidgetInstance(pr, bar, bar);
    }

    private static GtkWidgetInstance CreateStatusLine(StatusLine st)
    {
        var label = Gtk.gtk_label_new(string.Empty);
        Gtk.gtk_label_set_xalign(label, 0);
        return new GtkWidgetInstance(st, label, label);
    }

    // Pango markup requires escaping the same five XML special chars.
    private static string Escape(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return text
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&apos;");
    }
}
