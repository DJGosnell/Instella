using Instella.Installer.Runtime.UI.Widgets;

namespace Instella.Installer.Runtime.UI.Windows.Widgets;

/// <summary>
/// A single widget's slot in the vertical-stack layout — the bounding
/// rect the <see cref="Win32WidgetFactory"/> must place its controls
/// inside. Rects are in client-area pixels at the current window DPI.
/// </summary>
/// <remarks>
/// Compound widgets (e.g. <see cref="TextInput"/> = label + edit) use
/// the slot as their total extent and sub-position internally. The
/// layout engine doesn't need to know which widgets are compound — it
/// only uses the widget kind to pick a nominal height.
/// </remarks>
internal readonly record struct Win32LayoutSlot(
    Widget Widget,
    int Left,
    int Top,
    int Width,
    int Height);
