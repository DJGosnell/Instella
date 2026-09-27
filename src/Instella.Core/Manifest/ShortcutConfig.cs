namespace Instella.Core.Manifest;

/// <summary>
/// Configuration for shortcut creation.
/// </summary>
/// <param name="Desktop">Whether to create a desktop shortcut.</param>
/// <param name="StartMenu">Whether to create a start menu/applications menu shortcut.</param>
public sealed record ShortcutConfig(bool Desktop, bool StartMenu);
