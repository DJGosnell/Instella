namespace Instella.Core.Platform;

/// <summary>
/// Location where a shortcut can be created.
/// </summary>
public enum ShortcutLocation : byte
{
    /// <summary>Desktop shortcut.</summary>
    Desktop = 1,

    /// <summary>Start Menu (Windows) or Applications Menu (Linux/macOS).</summary>
    StartMenu = 2
}
