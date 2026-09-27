using System;
using System.Collections.Generic;
using Instella.Installer.Runtime.UI.Widgets;

namespace Instella.Installer.Runtime.UI.Windows.Widgets;

/// <summary>
/// Routes <c>WM_COMMAND</c> notifications produced by a page's
/// widgets back into the bound <see cref="PageState"/>. The pump is a
/// pure function of <c>(wParam, lParam)</c> and its bindings table; it
/// does not call into the OS event loop and can be driven
/// synchronously by tests.
/// </summary>
/// <remarks>
/// <para>Only reactive widgets produce notifications we care about:
/// <see cref="CheckBox"/> (BN_CLICKED), <see cref="RadioGroup"/>
/// options (BN_CLICKED), <see cref="TextInput"/> /
/// <see cref="FolderPicker"/> / <see cref="FilePicker"/> EDIT
/// (EN_CHANGE), and <see cref="Dropdown"/> COMBOBOX (CBN_SELCHANGE).
/// Browse-button clicks raise the <see cref="BrowseRequested"/> event
/// for the host to open the OS file / folder picker.</para>
/// <para>Unknown control IDs are silently ignored — the parent window
/// likely owns non-widget controls (footer buttons, etc.) that route
/// through a higher-level handler.</para>
/// </remarks>
internal sealed class Win32EventPump
{
    private readonly IReadOnlyDictionary<int, Win32ControlBinding> _bindings;
    private readonly PageState _state;

    /// <summary>
    /// Fires when the user clicks the Browse button on a
    /// <see cref="FolderPicker"/> or <see cref="FilePicker"/>. Hosts
    /// wire this to <see cref="FolderPickerDialog.ShowFolderPicker"/> or the
    /// corresponding <see cref="FilePicker"/> entry point.
    /// </summary>
    internal event Action<Win32WidgetInstance>? BrowseRequested;

    internal Win32EventPump(IReadOnlyDictionary<int, Win32ControlBinding> bindings, PageState state)
    {
        _bindings = bindings;
        _state = state;
    }

    /// <summary>
    /// Handle a <c>WM_COMMAND</c> notification. Returns <c>true</c>
    /// when the notification was routed to a widget (and thus should
    /// not fall through to <c>DefWindowProc</c>), <c>false</c> when
    /// the control ID is not one of ours.
    /// </summary>
    internal bool HandleCommand(nuint wParam, nint lParam)
    {
        var controlId = (int)(wParam & 0xFFFF);
        var notifCode = (int)((wParam >> 16) & 0xFFFF);

        if (!_bindings.TryGetValue(controlId, out var binding))
            return false;

        switch (binding.Role)
        {
            case Win32ControlRole.Browse:
                if (notifCode == BN.CLICKED) BrowseRequested?.Invoke(binding.Instance);
                return true;

            case Win32ControlRole.RadioOption:
                if (notifCode == BN.CLICKED) WriteRadioSelection(binding);
                return true;

            case Win32ControlRole.Primary:
                return DispatchPrimary(binding.Instance, notifCode);

            default:
                return false;
        }
    }

    private bool DispatchPrimary(Win32WidgetInstance instance, int notifCode)
    {
        switch (instance.Widget)
        {
            case CheckBox cb when notifCode == BN.CLICKED:
                WriteCheckBoxState(instance, cb);
                return true;

            case TextInput ti when notifCode == EN.CHANGE:
                WriteEditText(instance, ti.Id);
                return true;

            case FolderPicker fp when notifCode == EN.CHANGE:
                WriteEditText(instance, fp.Id);
                return true;

            case FilePicker fi when notifCode == EN.CHANGE:
                WriteEditText(instance, fi.Id);
                return true;

            case Dropdown dd when notifCode == CBN.SELCHANGE:
                WriteDropdownSelection(instance, dd);
                return true;

            default:
                // Recognised widget whose primary control doesn't fire events
                // (Heading, Paragraph, Progress, StatusLine, BrandImage).
                return true;
        }
    }

    private void WriteCheckBoxState(Win32WidgetInstance instance, CheckBox cb)
    {
        if (string.IsNullOrEmpty(cb.Id)) return;
        var checkState = (int)Win32.SendMessageW(instance.PrimaryHwnd, BM.GETCHECK, 0, 0);
        _state.Set(cb.Id, checkState == BST.CHECKED);
    }

    private void WriteEditText(Win32WidgetInstance instance, string? id)
    {
        if (string.IsNullOrEmpty(id)) return;
        var text = Win32.GetWindowText(instance.PrimaryHwnd);
        _state.Set(id, text);
    }

    private void WriteDropdownSelection(Win32WidgetInstance instance, Dropdown dd)
    {
        if (string.IsNullOrEmpty(dd.Id)) return;
        var index = (int)Win32.SendMessageW(instance.PrimaryHwnd, CB.GETCURSEL, 0, 0);
        if (index < 0 || index >= dd.Options.Count)
        {
            _state.Set(dd.Id, null);
            return;
        }
        _state.Set(dd.Id, dd.Options[index]);
    }

    private void WriteRadioSelection(Win32ControlBinding binding)
    {
        var instance = binding.Instance;
        if (instance.Widget is not RadioGroup group || string.IsNullOrEmpty(group.Id)) return;
        if (binding.OptionIndex < 0 || binding.OptionIndex >= instance.RadioOptions.Count) return;

        var option = instance.RadioOptions[binding.OptionIndex];
        _state.Set(group.Id, option.Value);
    }
}

/// <summary>Role a Win32 control plays inside a <see cref="Win32WidgetInstance"/>.</summary>
internal enum Win32ControlRole
{
    /// <summary>The main HWND whose state maps to <see cref="PageState"/>.</summary>
    Primary,
    /// <summary>The "Browse..." button on a FolderPicker / FilePicker.</summary>
    Browse,
    /// <summary>One radio option in a <see cref="RadioGroup"/>. Uses OptionIndex.</summary>
    RadioOption,
}

/// <summary>Entry in the pump's control-id → widget-instance lookup table.</summary>
internal readonly record struct Win32ControlBinding(
    Win32WidgetInstance Instance,
    Win32ControlRole Role,
    int OptionIndex = -1);
