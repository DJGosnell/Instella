using System;
using System.Collections.Generic;
using Instella.Installer.Runtime.UI.Widgets;

namespace Instella.Installer.Runtime.UI.Windows.Widgets;

/// <summary>
/// A materialized widget — the set of Win32 HWNDs that implement a
/// single <see cref="Widget"/> record on a page. The primary HWND is
/// the one whose state the event pump reads back into
/// <see cref="PageState"/> (for example, the EDIT for a
/// <see cref="TextInput"/>, the BUTTON for a <see cref="CheckBox"/>,
/// the COMBOBOX for a <see cref="Dropdown"/>).
/// </summary>
/// <remarks>
/// <para>Disposing the instance destroys every owned HWND and releases
/// any bound GDI resources. The factory guarantees that disposing
/// happens in reverse creation order within a page, so the OS cleans
/// up parent/child references consistently.</para>
/// </remarks>
internal sealed class Win32WidgetInstance : IDisposable
{
    /// <summary>The source widget record.</summary>
    internal Widget Widget { get; }

    /// <summary>
    /// Control ID of the HWND whose state change triggers a
    /// <see cref="PageState"/> write. For widgets without a value
    /// (<see cref="Heading"/>, <see cref="Paragraph"/>,
    /// <see cref="Progress"/>, <see cref="StatusLine"/>) this is the
    /// control ID of the single rendered HWND.
    /// </summary>
    internal int PrimaryControlId { get; }

    /// <summary>Primary HWND matching <see cref="PrimaryControlId"/>.</summary>
    internal nint PrimaryHwnd { get; }

    /// <summary>
    /// All HWNDs owned by this instance (labels, browse buttons, radio
    /// options). Used for visibility / enabled toggling and for cleanup.
    /// </summary>
    internal IReadOnlyList<nint> AllHwnds { get; }

    /// <summary>
    /// Control ID of the "Browse..." button on a
    /// <see cref="FolderPicker"/> or <see cref="FilePicker"/>. The
    /// event pump uses this to route a BN_CLICKED notification into
    /// the OS folder / file picker.
    /// </summary>
    internal int? BrowseButtonControlId { get; }

    /// <summary>
    /// One entry per radio option for a <see cref="RadioGroup"/>:
    /// the Win32 control ID of the radio button, its HWND, and the
    /// <see cref="RadioOption.Value"/> it represents.
    /// </summary>
    internal IReadOnlyList<RadioOptionBinding> RadioOptions { get; }

    /// <summary>
    /// GDI bitmap bound to this instance, if any. Only
    /// <see cref="BrandImage"/> widgets own a bitmap; disposal frees
    /// the HBITMAP handle.
    /// </summary>
    internal Win32Bitmap? Bitmap { get; }

    internal Win32WidgetInstance(
        Widget widget,
        int primaryControlId,
        nint primaryHwnd,
        IReadOnlyList<nint> allHwnds,
        int? browseButtonControlId = null,
        IReadOnlyList<RadioOptionBinding>? radioOptions = null,
        Win32Bitmap? bitmap = null)
    {
        Widget = widget;
        PrimaryControlId = primaryControlId;
        PrimaryHwnd = primaryHwnd;
        AllHwnds = allHwnds;
        BrowseButtonControlId = browseButtonControlId;
        RadioOptions = radioOptions ?? System.Array.Empty<RadioOptionBinding>();
        Bitmap = bitmap;
    }

    public void Dispose()
    {
        foreach (var hwnd in AllHwnds)
        {
            if (hwnd != 0) Win32.DestroyWindow(hwnd);
        }
        Bitmap?.Dispose();
    }
}

/// <summary>Binding of one radio option to its Win32 button control.</summary>
internal readonly record struct RadioOptionBinding(int ControlId, nint Hwnd, string Value);
