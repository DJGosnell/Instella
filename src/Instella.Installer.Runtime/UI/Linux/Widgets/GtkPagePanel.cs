using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using Instella.Installer.Runtime.Builders;
using Instella.Installer.Runtime.UI.Widgets;

namespace Instella.Installer.Runtime.UI.Linux.Widgets;

/// <summary>
/// Orchestrates the widgets of a single wizard page under GTK.
/// Unlike Win32 we don't compute pixel rects — GTK's <c>GtkBox</c>
/// handles sizing — but we still own widget lifecycle, state-change
/// reactivity, and dispose-ordering.
/// </summary>
/// <remarks>
/// <para>The panel's root is a vertical <c>GtkBox</c> whose children
/// are each widget instance's root. When
/// <see cref="PageState.StateChanged"/> fires we call
/// <c>gtk_widget_set_visible</c> / <c>gtk_widget_set_sensitive</c>
/// on each widget per its predicates — GTK automatically reflows
/// when a child's visible flag toggles, so hidden widgets vacate
/// their space.</para>
/// </remarks>
[SupportedOSPlatform("linux")]
internal sealed class GtkPagePanel : IDisposable
{
    private readonly PageSpec _spec;
    private readonly PageState _state;
    private readonly GtkEventPump _pump;
    private readonly List<GtkWidgetInstance> _instances;
    private readonly nint _rootBox;
    private bool _disposed;

    /// <summary>
    /// Fires when the user clicks the Browse button on a
    /// <see cref="FolderPicker"/> or <see cref="FilePicker"/>. The
    /// host wires this to the GTK file-chooser dialog.
    /// </summary>
    internal event Action<GtkWidgetInstance>? BrowseRequested;

    /// <summary>The root <c>GtkBox</c> that hosts all widgets.</summary>
    internal nint RootBox => _rootBox;

    /// <summary>The widget instances currently materialized.</summary>
    internal IReadOnlyList<GtkWidgetInstance> Instances => _instances;

    internal GtkPagePanel(PageSpec spec, PageState state)
    {
        _spec = spec;
        _state = state;
        _pump = new GtkEventPump(state);
        _pump.BrowseRequested += OnBrowseRequested;

        _rootBox = Gtk.gtk_box_new(Gtk.GTK_ORIENTATION_VERTICAL, GtkLayoutEngine.VerticalSpacing);
        ApplyMargins(_rootBox, GtkLayoutEngine.ContentPadding);

        var factory = new GtkWidgetFactory(state, _pump);
        _instances = new List<GtkWidgetInstance>(spec.Widgets.Count);

        foreach (var widget in spec.Widgets)
        {
            var instance = factory.Create(widget);
            _instances.Add(instance);
            // A text box (licence, error) takes the page's spare height, as on Windows.
            var fill = widget is ScrollableText ? 1 : 0;
            Gtk.gtk_box_pack_start(_rootBox, instance.RootWidget, fill, fill, 0);
        }

        ApplyVisibilityAndSensitivity();
        _state.StateChanged += OnStateChanged;
    }

    /// <summary>
    /// Re-evaluate <c>Visible</c> / <c>Enabled</c> predicates against
    /// the current <see cref="PageState"/>.
    /// </summary>
    internal void RefreshFromState() => ApplyVisibilityAndSensitivity();

    /// <summary>
    /// Evaluate <see cref="PageSpec.ContinueWhen"/> against current
    /// state. Returns <c>true</c> when the predicate is absent.
    /// </summary>
    internal bool CanContinue() => _spec.ContinueWhen is not { } continueWhen
        // A throwing ContinueWhen keeps the user on the page.
        || Runners.UserCode.Run(() => continueWhen(_state), $"page '{_spec.Id}' ContinueWhen", null, onError: false);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _state.StateChanged -= OnStateChanged;
        _pump.BrowseRequested -= OnBrowseRequested;

        for (int i = _instances.Count - 1; i >= 0; i--)
            _instances[i].Dispose();
        _instances.Clear();

        // The root box and its children are owned by the container that
        // packed them — destroying the enclosing window cascades. We
        // deliberately do not call gtk_widget_destroy on _rootBox here
        // to avoid double-free when the host's window closes.
    }

    private void ApplyVisibilityAndSensitivity()
    {
        foreach (var instance in _instances)
        {
            var widget = instance.Widget;
            var visible = widget.Visible is not { } isVisible
                || Runners.UserCode.Run(() => isVisible(_state), $"widget '{widget.Id}' Visible", null, onError: true);
            var sensitive = widget.Enabled is not { } isEnabled
                || Runners.UserCode.Run(() => isEnabled(_state), $"widget '{widget.Id}' Enabled", null, onError: false);

            Gtk.gtk_widget_set_visible(instance.RootWidget, visible ? 1 : 0);
            Gtk.gtk_widget_set_sensitive(instance.RootWidget, sensitive ? 1 : 0);
        }
    }

    private static void ApplyMargins(nint widget, int margin)
    {
        Gtk.gtk_widget_set_margin_start(widget, margin);
        Gtk.gtk_widget_set_margin_end(widget, margin);
        Gtk.gtk_widget_set_margin_top(widget, margin);
        Gtk.gtk_widget_set_margin_bottom(widget, margin);
    }

    private void OnStateChanged() => ApplyVisibilityAndSensitivity();

    private void OnBrowseRequested(GtkWidgetInstance instance) => BrowseRequested?.Invoke(instance);
}
