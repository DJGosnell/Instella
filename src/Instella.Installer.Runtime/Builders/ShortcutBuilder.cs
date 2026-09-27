using Instella.Core.Manifest;

namespace Instella.Installer.Runtime.Builders;

/// <summary>
/// Fluent configuration for desktop + start-menu shortcut creation. Obtained
/// via <see cref="InstallerBuilder.WithShortcuts"/>; the builder records the
/// final <see cref="ShortcutConfig"/> back on the parent.
/// </summary>
public sealed class ShortcutBuilder
{
    private bool _desktop;
    private bool _startMenu;

    internal ShortcutBuilder() { }

    /// <summary>Enable or disable the desktop shortcut. Default: disabled.</summary>
    public ShortcutBuilder Desktop(bool enabled = true)
    {
        _desktop = enabled;
        return this;
    }

    /// <summary>Enable or disable the Start Menu (Windows) / desktop entry (Linux) shortcut. Default: disabled.</summary>
    public ShortcutBuilder StartMenu(bool enabled = true)
    {
        _startMenu = enabled;
        return this;
    }

    internal ShortcutConfig Build() => new(_desktop, _startMenu);
}
