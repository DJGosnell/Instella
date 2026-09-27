using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using Instella.Installer.Runtime.Builders;
using Instella.Installer.Runtime.UI.Widgets;

namespace Instella.Installer.Runtime.UI.MacOS.Widgets;

/// <summary>
/// Orchestrates the widgets of a single wizard page under Cocoa.
/// Creates an NSView container, materializes every widget via
/// <see cref="CocoaWidgetFactory"/>, and re-evaluates
/// <c>Visible</c> / <c>Enabled</c> predicates when
/// <see cref="PageState"/> changes.
/// </summary>
/// <remarks>
/// <para>Layout allocates a slot for every widget up-front (via
/// <see cref="CocoaLayoutEngine.ComputeIgnoringVisible"/>) so runtime
/// visibility toggles are a pure <c>setHidden:</c> call — no
/// relayout, no NSView recreation. Matches the Win32 / GTK
/// subsystems.</para>
/// </remarks>
[SupportedOSPlatform("macos")]
internal sealed class CocoaPagePanel : IDisposable
{
    private readonly PageSpec _spec;
    private readonly PageState _state;
    private readonly CocoaEventPump _pump;
    private readonly List<CocoaWidgetInstance> _instances;
    private readonly nint _rootView;
    private bool _disposed;

    /// <summary>
    /// Fires when the user clicks the Browse button on a
    /// <see cref="FolderPicker"/> or <see cref="FilePicker"/>. The
    /// host wires this to <c>NSOpenPanel</c>.
    /// </summary>
    internal event Action<CocoaWidgetInstance>? BrowseRequested;

    /// <summary>The root NSView that hosts all widgets.</summary>
    internal nint RootView => _rootView;

    /// <summary>The widget instances currently materialized.</summary>
    internal IReadOnlyList<CocoaWidgetInstance> Instances => _instances;

    internal CocoaPagePanel(PageSpec spec, PageState state, int panelWidthPx, int panelHeightPx)
    {
        _spec = spec;
        _state = state;
        _pump = new CocoaEventPump(state);
        _pump.BrowseRequested += OnBrowseRequested;

        _rootView = NS.CreateView(new NSRect(0, 0, panelWidthPx, panelHeightPx));

        var factory = new CocoaWidgetFactory(_rootView, panelHeightPx, state, _pump);
        var slots = CocoaLayoutEngine.ComputeIgnoringVisible(spec.Widgets, panelWidthPx);

        _instances = new List<CocoaWidgetInstance>(spec.Widgets.Count);
        foreach (var slot in slots)
        {
            var instance = factory.Create(slot.Widget, slot);
            _instances.Add(instance);
            NS.AddSubview(_rootView, instance.RootView);
        }

        ApplyVisibilityAndEnabled();
        _state.StateChanged += OnStateChanged;
    }

    internal void RefreshFromState() => ApplyVisibilityAndEnabled();

    internal bool CanContinue() => _spec.ContinueWhen is not { } continueWhen
        // A throwing ContinueWhen keeps the user on the page.
        || Runners.UserCode.Run(() => continueWhen(_state), $"page '{_spec.Id}' ContinueWhen", null, onError: false);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _state.StateChanged -= OnStateChanged;
        _pump.BrowseRequested -= OnBrowseRequested;

        // Unregister control bindings before releasing delegate targets
        // so no pending dispatch finds a stale entry.
        foreach (var instance in _instances)
        {
            _pump.UnregisterControl(instance.PrimaryView);
            if (instance.BrowseButtonView != 0) _pump.UnregisterControl(instance.BrowseButtonView);
            foreach (var r in instance.RadioOptions) _pump.UnregisterControl(r.View);
        }

        for (int i = _instances.Count - 1; i >= 0; i--)
            _instances[i].Dispose();
        _instances.Clear();

        _pump.ReleaseSharedDelegate();

        // _rootView is retained by the window's contentView; releasing the
        // window cascades. Don't release here.
    }

    private void ApplyVisibilityAndEnabled()
    {
        foreach (var instance in _instances)
        {
            var widget = instance.Widget;
            var visible = widget.Visible is not { } isVisible
                || Runners.UserCode.Run(() => isVisible(_state), $"widget '{widget.Id}' Visible", null, onError: true);
            var enabled = widget.Enabled is not { } isEnabled
                || Runners.UserCode.Run(() => isEnabled(_state), $"widget '{widget.Id}' Enabled", null, onError: false);

            NS.SetHidden(instance.RootView, !visible);
            // NSControl is the superclass of NSButton / NSTextField / NSPopUpButton;
            // setEnabled: lives there. Non-control views (NSView, NSProgressIndicator)
            // will silently no-op on setEnabled: (unrecognized selector is a crash in
            // strict ObjC, but AppKit's NSView has setEnabled: via responder chain on
            // some subclasses — we guard by only disabling controls).
            foreach (var v in IterateEnableable(instance))
            {
                if (v != 0) NS.SetEnabled(v, enabled);
            }
        }
    }

    private static IEnumerable<nint> IterateEnableable(CocoaWidgetInstance instance)
    {
        // Only widgets whose primary view is an NSControl subclass should
        // receive setEnabled:. The event pump's widget kinds that write to
        // PageState all produce NSControl subclasses; others (Heading,
        // Paragraph, Progress, etc.) don't need to be disabled and we
        // skip them entirely.
        switch (instance.Widget)
        {
            case CheckBox:
            case TextInput:
            case Dropdown:
            case FolderPicker:
            case FilePicker:
                yield return instance.PrimaryView;
                if (instance.BrowseButtonView != 0) yield return instance.BrowseButtonView;
                break;
            case RadioGroup:
                foreach (var binding in instance.RadioOptions)
                    yield return binding.View;
                break;
        }
    }

    private void OnStateChanged() => ApplyVisibilityAndEnabled();

    private void OnBrowseRequested(CocoaWidgetInstance instance) => BrowseRequested?.Invoke(instance);
}
