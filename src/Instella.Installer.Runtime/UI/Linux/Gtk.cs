using System.Runtime.InteropServices;

namespace Instella.Installer.Runtime.UI.Linux;

internal static partial class Gtk
{
    private const string Lib = "libgtk-3.so.0";
    private const string GLib = "libglib-2.0.so.0";
    private const string GObjectLib = "libgobject-2.0.so.0";
    private const string GdkPixbufLib = "libgdk_pixbuf-2.0.so.0";
    private const string GioLib = "libgio-2.0.so.0";

    // Initialization. gtk_init aborts the process when there is no display; gtk_init_check
    // returns false instead.
    [LibraryImport(Lib)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool gtk_init_check(ref int argc, ref nint argv);

    [LibraryImport(Lib)]
    internal static partial void gtk_main();

    [LibraryImport(Lib)]
    internal static partial void gtk_main_quit();

    // Window
    [LibraryImport(Lib)]
    internal static partial nint gtk_window_new(int type);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void gtk_window_set_title(nint window, string title);

    [LibraryImport(Lib)]
    internal static partial void gtk_window_set_default_size(nint window, int width, int height);

    [LibraryImport(Lib)]
    internal static partial void gtk_window_set_resizable(nint window, int resizable);

    [LibraryImport(Lib)]
    internal static partial void gtk_window_set_position(nint window, int position);

    // Containers
    [LibraryImport(Lib)]
    internal static partial nint gtk_box_new(int orientation, int spacing);

    [LibraryImport(Lib)]
    internal static partial void gtk_box_pack_start(nint box, nint child, int expand, int fill, uint padding);

    [LibraryImport(Lib)]
    internal static partial void gtk_box_pack_end(nint box, nint child, int expand, int fill, uint padding);

    [LibraryImport(Lib)]
    internal static partial nint gtk_stack_new();

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void gtk_stack_add_named(nint stack, nint child, string name);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void gtk_stack_set_visible_child_name(nint stack, string name);

    // Widgets
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint gtk_label_new(string? text);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void gtk_label_set_markup(nint label, string markup);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void gtk_label_set_text(nint label, string text);

    [LibraryImport(Lib)]
    internal static partial void gtk_label_set_xalign(nint label, float xalign);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint gtk_button_new_with_label(string label);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void gtk_button_set_label(nint button, string label);

    [LibraryImport(Lib)]
    internal static partial nint gtk_entry_new();

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void gtk_entry_set_text(nint entry, string text);

    [LibraryImport(Lib)]
    internal static partial nint gtk_entry_get_text(nint entry);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint gtk_check_button_new_with_label(string label);

    [LibraryImport(Lib)]
    internal static partial int gtk_toggle_button_get_active(nint button);

    [LibraryImport(Lib)]
    internal static partial void gtk_toggle_button_set_active(nint button, int active);

    [LibraryImport(Lib)]
    internal static partial nint gtk_progress_bar_new();

    [LibraryImport(Lib)]
    internal static partial void gtk_progress_bar_set_fraction(nint pbar, double fraction);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void gtk_progress_bar_set_text(nint pbar, string? text);

    [LibraryImport(Lib)]
    internal static partial void gtk_progress_bar_set_show_text(nint pbar, int show);

    // Visibility and sensitivity
    [LibraryImport(Lib)]
    internal static partial void gtk_widget_show_all(nint widget);

    [LibraryImport(Lib)]
    internal static partial void gtk_widget_show(nint widget);

    [LibraryImport(Lib)]
    internal static partial void gtk_widget_hide(nint widget);

    [LibraryImport(Lib)]
    internal static partial void gtk_widget_set_sensitive(nint widget, int sensitive);

    [LibraryImport(Lib)]
    internal static partial void gtk_widget_set_size_request(nint widget, int width, int height);

    [LibraryImport(Lib)]
    internal static partial void gtk_widget_set_margin_start(nint widget, int margin);

    [LibraryImport(Lib)]
    internal static partial void gtk_widget_set_margin_end(nint widget, int margin);

    [LibraryImport(Lib)]
    internal static partial void gtk_widget_set_margin_top(nint widget, int margin);

    [LibraryImport(Lib)]
    internal static partial void gtk_widget_set_margin_bottom(nint widget, int margin);

    [LibraryImport(Lib)]
    internal static partial void gtk_widget_set_halign(nint widget, int align);

    [LibraryImport(Lib)]
    internal static partial void gtk_widget_set_valign(nint widget, int align);

    [LibraryImport(Lib)]
    internal static partial void gtk_widget_set_hexpand(nint widget, int expand);

    [LibraryImport(Lib)]
    internal static partial void gtk_container_add(nint container, nint widget);

    [LibraryImport(Lib)]
    internal static partial void gtk_widget_destroy(nint widget);

    // File chooser
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint gtk_file_chooser_dialog_new(string title, nint parent, int action, nint firstButton);

    [LibraryImport(Lib)]
    internal static partial nint gtk_file_chooser_get_filename(nint chooser);

    [LibraryImport(Lib)]
    internal static partial int gtk_dialog_run(nint dialog);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void gtk_dialog_add_button(nint dialog, string buttonText, int responseId);

    // Signals
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial ulong g_signal_connect_data(nint instance, string signal, nint handler, nint data, nint destroyData, int flags);

    // Idle callback (UI thread marshaling)
    [LibraryImport(GLib)]
    internal static partial uint g_idle_add(nint function, nint data);

    // Memory
    [LibraryImport(GLib)]
    internal static partial void g_free(nint mem);

    // CSS styling
    [LibraryImport(Lib)]
    internal static partial nint gtk_css_provider_new();

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void gtk_css_provider_load_from_data(nint provider, string data, int length, nint error);

    [LibraryImport(Lib)]
    internal static partial void gtk_style_context_add_provider_for_screen(nint screen, nint provider, uint priority);

    [LibraryImport(Lib)]
    internal static partial nint gdk_screen_get_default();

    [LibraryImport(Lib)]
    internal static partial nint gtk_widget_get_style_context(nint widget);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void gtk_style_context_add_class(nint context, string className);

    // Additional widget types used by the widget renderer

    [LibraryImport(Lib)]
    internal static partial nint gtk_scrolled_window_new(nint hadjustment, nint vadjustment);

    [LibraryImport(Lib)]
    internal static partial void gtk_scrolled_window_set_policy(nint sw, int hscrollbar, int vscrollbar);

    [LibraryImport(Lib)]
    internal static partial nint gtk_text_view_new();

    [LibraryImport(Lib)]
    internal static partial nint gtk_text_view_get_buffer(nint textView);

    [LibraryImport(Lib)]
    internal static partial void gtk_text_view_set_editable(nint textView, int editable);

    [LibraryImport(Lib)]
    internal static partial void gtk_text_view_set_wrap_mode(nint textView, int wrapMode);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void gtk_text_buffer_set_text(nint buffer, string text, int length);

    [LibraryImport(Lib)]
    internal static partial nint gtk_combo_box_text_new();

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void gtk_combo_box_text_append_text(nint combo, string text);

    [LibraryImport(Lib)]
    internal static partial int gtk_combo_box_get_active(nint combo);

    [LibraryImport(Lib)]
    internal static partial void gtk_combo_box_set_active(nint combo, int index);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint gtk_radio_button_new_with_label(nint group, string label);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint gtk_radio_button_new_with_label_from_widget(nint group, string label);

    [LibraryImport(Lib)]
    internal static partial nint gtk_radio_button_get_group(nint radioButton);

    [LibraryImport(Lib)]
    internal static partial nint gtk_image_new();

    [LibraryImport(Lib)]
    internal static partial nint gtk_image_new_from_pixbuf(nint pixbuf);

    [LibraryImport(Lib)]
    internal static partial void gtk_image_set_from_pixbuf(nint image, nint pixbuf);

    [LibraryImport(Lib)]
    internal static partial void gtk_widget_set_visible(nint widget, int visible);

    // gdk-pixbuf

    [LibraryImport(GdkPixbufLib)]
    internal static partial nint gdk_pixbuf_loader_new();

    [LibraryImport(GdkPixbufLib)]
    internal static partial int gdk_pixbuf_loader_write(nint loader, in byte buf, nuint count, nint error);

    [LibraryImport(GdkPixbufLib)]
    internal static partial int gdk_pixbuf_loader_close(nint loader, nint error);

    [LibraryImport(GdkPixbufLib)]
    internal static partial nint gdk_pixbuf_loader_get_pixbuf(nint loader);

    [LibraryImport(GdkPixbufLib, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint gdk_pixbuf_new_from_file(string filename, nint error);

    [LibraryImport(GdkPixbufLib)]
    internal static partial int gdk_pixbuf_get_width(nint pixbuf);

    [LibraryImport(GdkPixbufLib)]
    internal static partial int gdk_pixbuf_get_height(nint pixbuf);

    // GObject

    [LibraryImport(GObjectLib)]
    internal static partial void g_object_unref(nint obj);

    [LibraryImport(GObjectLib)]
    internal static partial void g_signal_handler_disconnect(nint instance, ulong handlerId);

    // Constants
    internal const int GTK_WINDOW_TOPLEVEL = 0;
    internal const int GTK_ORIENTATION_VERTICAL = 1;
    internal const int GTK_ORIENTATION_HORIZONTAL = 0;
    internal const int GTK_WIN_POS_CENTER = 1;
    internal const int GTK_FILE_CHOOSER_ACTION_SELECT_FOLDER = 2;
    internal const int GTK_RESPONSE_ACCEPT = -3;
    internal const int GTK_RESPONSE_CANCEL = -6;
    internal const int GTK_ALIGN_START = 1;
    internal const int GTK_ALIGN_END = 2;
    internal const int GTK_ALIGN_CENTER = 3;
    internal const int GTK_ALIGN_FILL = 4;

    // GtkPolicyType — auto-show scrollbars only when content overflows.
    internal const int GTK_POLICY_NEVER = 2;
    internal const int GTK_POLICY_AUTOMATIC = 1;

    // GtkWrapMode — wrap on word boundaries when space allows, otherwise on characters.
    internal const int GTK_WRAP_WORD_CHAR = 3;
    internal const int GTK_WRAP_WORD = 2;
    internal const int GTK_WRAP_NONE = 0;

    // G_SOURCE_REMOVE sentinel — used as the idle-callback return value.
    internal const int G_SOURCE_REMOVE = 0;
}
