using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Instella.Installer.Runtime.Builders;
using Instella.Installer.Runtime.UI;
using Instella.Installer.Runtime.UI.Widgets;

namespace Instella.Installer.Runtime.UI.Linux.Widgets;

/// <summary>
/// Outcome of running a <see cref="GtkWidgetHost"/> main loop.
/// </summary>
internal enum GtkWidgetHostOutcome
{
    /// <summary>User navigated past the last page via Continue.</summary>
    Completed,
    /// <summary>User clicked Cancel or closed the window.</summary>
    Cancelled,
}

/// <summary>
/// Standalone GTK host window that drives a multi-page wizard built
/// from <see cref="PageSpec"/> records; the GTK counterpart of
/// Win32WidgetHost.
/// </summary>
/// <remarks>
/// <para>Owns a <c>GtkWindow</c>, a <c>GtkStack</c> for page swap, a
/// footer <c>GtkBox</c> with Cancel / Back / Continue, and a
/// <see cref="GtkPagePanel"/> per page. PageState objects persist
/// across navigation so user input survives Back/Forward.</para>
/// </remarks>
[SupportedOSPlatform("linux")]
internal sealed class GtkWidgetHost : IDisposable
{
    private readonly string _title;
    private readonly IReadOnlyList<PageSpec> _pages;
    private readonly IReadOnlyList<PageState> _pageStates;

    private nint _window;
    private nint _stack;
    private nint _btnCancel, _btnBack, _btnContinue;
    private nint _previewBanner;
    private readonly List<GtkPagePanel> _panels = new();
    private int _currentPageIndex;
    private GtkWidgetHostOutcome _outcome = GtkWidgetHostOutcome.Cancelled;
    private GCHandle _selfHandle;
    private bool _disposed;

    /// <summary>
    /// When <c>true</c>, the window title gets a <c>" — PREVIEW"</c> suffix
    /// and a thin banner reading "Preview mode — no changes will be made" is
    /// packed above the page stack. Set only by <see cref="Runners.PreviewModeRunner"/>.
    /// </summary>
    internal bool IsPreview { get; init; }

    internal GtkWidgetHost(string title, IReadOnlyList<PageSpec> pages, IReadOnlyList<PageState> pageStates)
    {
        if (pages.Count == 0) throw new ArgumentException("At least one page is required.", nameof(pages));
        if (pages.Count != pageStates.Count)
            throw new ArgumentException("pages and pageStates must be parallel lists.", nameof(pageStates));

        _title = title;
        _pages = pages;
        _pageStates = pageStates;
    }

    /// <summary>The toplevel GtkWindow pointer. 0 until construction, 0 after dispose.</summary>
    internal nint Window => _window;

    /// <summary>Current page index (0-based). Stable for tests to query.</summary>
    internal int CurrentPageIndex => _currentPageIndex;

    /// <summary>Banner GtkLabel pointer when <see cref="IsPreview"/> is true; 0 otherwise. Test-only.</summary>
    internal nint PreviewBanner => _previewBanner;

    private static bool? _initialized;

    /// <summary>
    /// Initialises GTK once. False when no display is available (no X11/Wayland session):
    /// callers fall back to headless instead of letting <c>gtk_init</c> abort the process.
    /// </summary>
    internal static bool TryInitialize()
    {
        if (_initialized is { } done)
            return done;
        int argc = 0;
        nint argv = 0;
        _initialized = Gtk.gtk_init_check(ref argc, ref argv);
        return _initialized.Value;
    }

    /// <summary>Initialise GTK, construct the window, show it, run the main loop, return outcome.</summary>
    /// <exception cref="InvalidOperationException">No display is available.</exception>
    internal GtkWidgetHostOutcome Run()
    {
        if (!TryInitialize())
            throw new InvalidOperationException("GTK could not initialise: no display is available.");

        CreateWindow();
        Gtk.gtk_widget_show_all(_window);
        ApplyPageVisibility();

        Gtk.gtk_main();
        return _outcome;
    }

    /// <summary>
    /// Create the window without showing it or running the main loop —
    /// used by headless tests that drive navigation synchronously.
    /// </summary>
    internal void CreateHeadless()
    {
        if (!TryInitialize())
            throw new InvalidOperationException("GTK could not initialise: no display is available.");

        CreateWindow();
    }

    /// <summary>Navigate to <paramref name="pageIndex"/>. Exposed for tests.</summary>
    internal void NavigateTo(int pageIndex)
    {
        if (pageIndex < 0 || pageIndex >= _pages.Count) return;
        _currentPageIndex = pageIndex;
        Gtk.gtk_stack_set_visible_child_name(_stack, PageName(pageIndex));
        UpdateFooterButtons();
    }

    /// <summary>Simulate a Continue click. Exposed for tests.</summary>
    internal void SimulateContinue()
    {
        if (_panels[_currentPageIndex].CanContinue() == false) return;
        if (_currentPageIndex + 1 < _pages.Count)
        {
            NavigateTo(_currentPageIndex + 1);
        }
        else
        {
            _outcome = GtkWidgetHostOutcome.Completed;
            SafeMainQuit();
        }
    }

    /// <summary>Simulate a Cancel click. Exposed for tests.</summary>
    internal void SimulateCancel()
    {
        _outcome = GtkWidgetHostOutcome.Cancelled;
        SafeMainQuit();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var panel in _panels) panel.Dispose();
        _panels.Clear();

        if (_window != 0)
        {
            try { Gtk.gtk_widget_destroy(_window); }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            _window = 0;
        }
        if (_selfHandle.IsAllocated) _selfHandle.Free();
    }

    private unsafe void CreateWindow()
    {
        _selfHandle = GCHandle.Alloc(this);
        var data = GCHandle.ToIntPtr(_selfHandle);

        _window = Gtk.gtk_window_new(Gtk.GTK_WINDOW_TOPLEVEL);
        Gtk.gtk_window_set_title(_window, PreviewMarker.DecorateTitle(_title, IsPreview));
        Gtk.gtk_window_set_default_size(_window, 600, 460);
        Gtk.gtk_window_set_resizable(_window, 0);
        Gtk.gtk_window_set_position(_window, Gtk.GTK_WIN_POS_CENTER);

        Gtk.g_signal_connect_data(_window, "destroy",
            (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&OnWindowDestroy, data, 0, 0);

        var mainBox = Gtk.gtk_box_new(Gtk.GTK_ORIENTATION_VERTICAL, 0);
        Gtk.gtk_container_add(_window, mainBox);

        if (IsPreview)
        {
            _previewBanner = Gtk.gtk_label_new(PreviewMarker.BannerText);
            // Pack the banner above the page stack so it's present on every page
            // and visually unambiguous. Setting a fixed height on the label keeps
            // layout consistent with the Win32 banner.
            Gtk.gtk_widget_set_size_request(_previewBanner, -1, PreviewMarker.BannerHeight);
            Gtk.gtk_box_pack_start(mainBox, _previewBanner, 0, 0, 0);
        }

        _stack = Gtk.gtk_stack_new();
        Gtk.gtk_widget_set_hexpand(_stack, 1);
        Gtk.gtk_widget_set_valign(_stack, Gtk.GTK_ALIGN_FILL);
        Gtk.gtk_box_pack_start(mainBox, _stack, 1, 1, 0);

        for (int i = 0; i < _pages.Count; i++)
        {
            var panel = new GtkPagePanel(_pages[i], _pageStates[i]);
            panel.BrowseRequested += OnBrowseRequested;
            _panels.Add(panel);
            Gtk.gtk_stack_add_named(_stack, panel.RootBox, PageName(i));
        }

        Gtk.gtk_box_pack_end(mainBox, CreateFooter(data), 0, 0, 8);

        foreach (var state in _pageStates)
            state.StateChanged += UpdateFooterButtons;
    }

    private unsafe nint CreateFooter(nint data)
    {
        var footer = Gtk.gtk_box_new(Gtk.GTK_ORIENTATION_HORIZONTAL, 8);
        Gtk.gtk_widget_set_margin_start(footer, 16);
        Gtk.gtk_widget_set_margin_end(footer, 16);
        Gtk.gtk_widget_set_margin_top(footer, 8);
        Gtk.gtk_widget_set_margin_bottom(footer, 16);

        _btnCancel = Gtk.gtk_button_new_with_label("Cancel");
        Gtk.g_signal_connect_data(_btnCancel, "clicked",
            (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&OnCancelClicked, data, 0, 0);
        Gtk.gtk_box_pack_start(footer, _btnCancel, 0, 0, 0);

        var spacer = Gtk.gtk_label_new(null);
        Gtk.gtk_widget_set_hexpand(spacer, 1);
        Gtk.gtk_box_pack_start(footer, spacer, 1, 1, 0);

        _btnBack = Gtk.gtk_button_new_with_label("< Back");
        Gtk.g_signal_connect_data(_btnBack, "clicked",
            (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&OnBackClicked, data, 0, 0);
        Gtk.gtk_box_pack_start(footer, _btnBack, 0, 0, 0);

        _btnContinue = Gtk.gtk_button_new_with_label("Continue >");
        Gtk.g_signal_connect_data(_btnContinue, "clicked",
            (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&OnContinueClicked, data, 0, 0);
        Gtk.gtk_box_pack_start(footer, _btnContinue, 0, 0, 0);

        return footer;
    }

    private void ApplyPageVisibility()
    {
        Gtk.gtk_stack_set_visible_child_name(_stack, PageName(_currentPageIndex));
        UpdateFooterButtons();
    }

    private void UpdateFooterButtons()
    {
        if (_btnBack != 0)
            Gtk.gtk_widget_set_sensitive(_btnBack, _currentPageIndex > 0 ? 1 : 0);
        if (_btnContinue != 0)
        {
            var panel = _currentPageIndex < _panels.Count ? _panels[_currentPageIndex] : null;
            Gtk.gtk_widget_set_sensitive(_btnContinue, (panel?.CanContinue() ?? true) ? 1 : 0);
            var isLast = _currentPageIndex == _pages.Count - 1;
            Gtk.gtk_button_set_label(_btnContinue, isLast ? "Finish" : "Continue >");
        }
    }

    private static string PageName(int i) => $"page-{i}";

    private void OnBrowseRequested(GtkWidgetInstance instance)
    {
        // FolderPicker → open GtkFileChooserDialog. FilePicker deferred.
        if (instance.Widget is not FolderPicker) return;

        var dialog = Gtk.gtk_file_chooser_dialog_new(
            "Select folder", _window, Gtk.GTK_FILE_CHOOSER_ACTION_SELECT_FOLDER, 0);
        Gtk.gtk_dialog_add_button(dialog, "Cancel", Gtk.GTK_RESPONSE_CANCEL);
        Gtk.gtk_dialog_add_button(dialog, "Select", Gtk.GTK_RESPONSE_ACCEPT);

        var response = Gtk.gtk_dialog_run(dialog);
        if (response == Gtk.GTK_RESPONSE_ACCEPT)
        {
            var filename = Gtk.gtk_file_chooser_get_filename(dialog);
            if (filename != 0)
            {
                var path = Marshal.PtrToStringUTF8(filename) ?? string.Empty;
                Gtk.g_free(filename);
                Gtk.gtk_entry_set_text(instance.PrimaryWidget, path);
            }
        }
        Gtk.gtk_widget_destroy(dialog);
    }

    private static void SafeMainQuit()
    {
        try { Gtk.gtk_main_quit(); }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    // Static signal callbacks dispatch via GCHandle.

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnWindowDestroy(nint widget, nint data)
    {
        var host = (GtkWidgetHost?)GCHandle.FromIntPtr(data).Target;
        if (host is not null) host._outcome = GtkWidgetHostOutcome.Cancelled;
        SafeMainQuit();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnCancelClicked(nint widget, nint data)
    {
        var host = (GtkWidgetHost?)GCHandle.FromIntPtr(data).Target;
        host?.SimulateCancel();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnBackClicked(nint widget, nint data)
    {
        var host = (GtkWidgetHost?)GCHandle.FromIntPtr(data).Target;
        if (host is not null && host._currentPageIndex > 0)
            host.NavigateTo(host._currentPageIndex - 1);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnContinueClicked(nint widget, nint data)
    {
        var host = (GtkWidgetHost?)GCHandle.FromIntPtr(data).Target;
        host?.SimulateContinue();
    }
}
