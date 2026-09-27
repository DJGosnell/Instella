using System;
using System.Collections.Generic;
using Instella.Installer.Runtime.Builders;
using Instella.Installer.Runtime.UI.Widgets;

namespace Instella.Installer.Runtime.UI.Windows.Widgets;

/// <summary>
/// Orchestrates the widgets of a single wizard page: materializes
/// them on construction using a fixed all-visible layout, routes
/// <c>WM_COMMAND</c> notifications via <see cref="Win32EventPump"/>,
/// and re-evaluates <c>Visible</c> / <c>Enabled</c> predicates when
/// <see cref="PageState"/> changes.
/// </summary>
/// <remarks>
/// <para>Layout is computed once at construction against the widgets'
/// static record shape (text length, option counts). A widget whose
/// <see cref="Widget.Visible"/> predicate returns <c>false</c> is
/// hidden in place — its slot remains reserved and later widgets stay
/// anchored. This matches the DESIGN-phase "no flex, no grid" rule
/// and keeps state-change handling O(N) and allocation-free.</para>
/// <para>The panel is a logical region of the parent window — not a
/// separate HWND — so it does not introduce an extra control to the
/// tab order. It takes a bounding rect and lays widgets out inside
/// it.</para>
/// </remarks>
internal sealed class Win32PagePanel : IDisposable
{
    private readonly nint _parentHwnd;
    private readonly PageSpec _spec;
    private readonly PageState _state;
    private readonly Win32WidgetFactory _factory;
    private readonly List<Win32WidgetInstance> _instances;
    private readonly Dictionary<int, Win32ControlBinding> _bindings;
    private readonly Win32EventPump _pump;
    private bool _disposed;

    /// <summary>
    /// Fires when the user clicks the Browse button on a
    /// <see cref="FolderPicker"/> or <see cref="FilePicker"/>. Wiring
    /// this into the OS dialog is the host's responsibility.
    /// </summary>
    internal event Action<Win32WidgetInstance>? BrowseRequested;

    /// <summary>The widget instances currently materialized for this page.</summary>
    internal IReadOnlyList<Win32WidgetInstance> Instances => _instances;

    /// <summary>The control-id → binding dictionary consumed by the event pump.</summary>
    internal IReadOnlyDictionary<int, Win32ControlBinding> Bindings => _bindings;

    internal Win32PagePanel(
        nint parentHwnd,
        PageSpec spec,
        PageState state,
        nint hInstance,
        nint bodyFont,
        nint headerFont,
        int panelLeft,
        int panelTop,
        int panelWidth,
        int dpi,
        int firstControlId = 1000,
        int? panelHeight = null)
    {
        _parentHwnd = parentHwnd;
        _spec = spec;
        _state = state;

        var effectiveDpi = dpi <= 0 ? Win32DpiAware.BaseDpi : dpi;

        _factory = new Win32WidgetFactory(parentHwnd, hInstance, bodyFont, headerFont, firstControlId);
        _instances = new List<Win32WidgetInstance>(spec.Widgets.Count);
        _bindings = new Dictionary<int, Win32ControlBinding>(spec.Widgets.Count * 2);

        MaterializeAll(panelLeft, panelTop, panelWidth, panelHeight, effectiveDpi);
        _pump = new Win32EventPump(_bindings, state);
        _pump.BrowseRequested += OnBrowseRequested;

        ApplyVisibilityAndEnabled();
        _state.StateChanged += OnStateChanged;
    }

    /// <summary>
    /// Route a <c>WM_COMMAND</c> notification into the widget
    /// subsystem. Returns <c>true</c> when the notification targeted
    /// one of our widgets.
    /// </summary>
    internal bool HandleCommand(nuint wParam, nint lParam) => _pump.HandleCommand(wParam, lParam);

    /// <summary>
    /// Re-evaluate <c>Visible</c> / <c>Enabled</c> predicates against
    /// the current <see cref="PageState"/>. Exposed for hosts that
    /// want to force a refresh without waiting for a
    /// <see cref="PageState.StateChanged"/> event.
    /// </summary>
    internal void RefreshFromState()
    {
        ApplyVisibilityAndEnabled();
        ApplyStateReactiveWidgetValues();
    }

    /// <summary>
    /// Evaluate the spec's <see cref="PageSpec.ContinueWhen"/>
    /// predicate against current state. Returns <c>true</c> when the
    /// predicate is absent (default is "always continuable").
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
        _bindings.Clear();
    }

    private void MaterializeAll(int panelLeft, int panelTop, int panelWidth, int? panelHeight, int dpi)
    {
        // Allocate slots for every widget regardless of Visible so that
        // toggling Visible at runtime is a pure ShowWindow call — no
        // relayout, no HWND recreation.
        var slots = Win32LayoutEngine.ComputeIgnoringVisible(_spec.Widgets, panelWidth, dpi, panelHeight);
        var slotsByWidget = new Dictionary<Widget, Win32LayoutSlot>(ReferenceEqualityComparer.Instance);
        foreach (var slot in slots) slotsByWidget[slot.Widget] = slot;

        foreach (var widget in _spec.Widgets)
        {
            if (!slotsByWidget.TryGetValue(widget, out var slot))
                continue;

            var placementSlot = TranslateToPanel(slot, panelLeft, panelTop);
            var instance = _factory.Create(widget, placementSlot, _state);
            _instances.Add(instance);
            RegisterBindings(instance);
        }
    }

    private void RegisterBindings(Win32WidgetInstance instance)
    {
        _bindings[instance.PrimaryControlId] = new Win32ControlBinding(instance, Win32ControlRole.Primary);

        if (instance.BrowseButtonControlId is int browseId)
            _bindings[browseId] = new Win32ControlBinding(instance, Win32ControlRole.Browse);

        for (int i = 0; i < instance.RadioOptions.Count; i++)
            _bindings[instance.RadioOptions[i].ControlId] = new Win32ControlBinding(instance, Win32ControlRole.RadioOption, i);
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

            foreach (var hwnd in instance.AllHwnds)
            {
                if (hwnd == 0) continue;
                _ = Win32.ShowWindow(hwnd, visible ? SW.SHOW : SW.HIDE);
                _ = Win32.EnableWindow(hwnd, enabled ? 1 : 0);
            }
        }
    }

    private static Win32LayoutSlot TranslateToPanel(Win32LayoutSlot slot, int panelLeft, int panelTop)
    {
        return new Win32LayoutSlot(slot.Widget, panelLeft + slot.Left, panelTop + slot.Top, slot.Width, slot.Height);
    }

    private void OnStateChanged()
    {
        ApplyVisibilityAndEnabled();
        ApplyStateReactiveWidgetValues();
    }

    /// <summary>
    /// Apply <see cref="PageState"/>-driven updates to widgets that display a
    /// runtime value: <see cref="Progress"/> reads a double fraction from the
    /// state under its <see cref="Widget.Id"/> and updates the progress bar
    /// position; <see cref="StatusLine"/> reads a string and updates the
    /// static control's text. Widgets with no id or no state entry are
    /// skipped. Keeps the interactive install flow's progress plumbing
    /// declarative: the runner writes PageState, the renderer refreshes.
    /// </summary>
    private void ApplyStateReactiveWidgetValues()
    {
        foreach (var instance in _instances)
        {
            var id = instance.Widget.Id;
            if (string.IsNullOrEmpty(id)) continue;

            switch (instance.Widget)
            {
                case Progress:
                    var position = ProgressBarPosition(_state.Get<double>(id, 0.0));
                    if (instance.PrimaryHwnd != 0)
                        Win32.SendMessageW(instance.PrimaryHwnd, PBM.SETPOS, (nuint)position, 0);
                    break;

                case StatusLine:
                    var text = _state.Get<string>(id, string.Empty) ?? string.Empty;
                    if (instance.PrimaryHwnd != 0)
                        Win32.SetWindowTextW(instance.PrimaryHwnd, text);
                    break;

                // A ScrollableText with an id shows the state's text once there is one (the
                // Progress page's error details); until then its own text.
                case ScrollableText when _state.TryGet<string>(id, out var details) && details is not null:
                    if (instance.PrimaryHwnd != 0)
                        Win32.SetWindowTextW(instance.PrimaryHwnd, details.ReplaceLineEndings("\r\n"));
                    break;
            }
        }
    }

    private void OnBrowseRequested(Win32WidgetInstance instance) => BrowseRequested?.Invoke(instance);

    /// <summary>
    /// Map a 0..1 progress fraction to the <c>PBM_SETPOS</c> range (0..1000)
    /// the progress bar uses, clamping out-of-range inputs. Pure; extracted for
    /// unit testing without a live control.
    /// </summary>
    internal static int ProgressBarPosition(double fraction)
    {
        var clamped = fraction < 0 ? 0 : (fraction > 1 ? 1 : fraction);
        return (int)(clamped * 1000);
    }
}
