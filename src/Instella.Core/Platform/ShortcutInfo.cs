namespace Instella.Core.Platform;

/// <summary>
/// Information needed to create a shortcut.
/// </summary>
/// <param name="Name">Display name of the shortcut.</param>
/// <param name="TargetPath">Full path to the executable.</param>
/// <param name="IconPath">Optional path to the icon file.</param>
/// <param name="PerUser">The current user's Desktop / Start menu (true) or the all-users ones (false).</param>
/// <param name="Arguments">Optional command-line arguments.</param>
/// <param name="Location">Where to create the shortcut.</param>
/// <param name="WorkingDirectory">Optional working directory for the shortcut.</param>
/// <param name="Description">Optional description/tooltip for the shortcut.</param>
public sealed record ShortcutInfo(
    string Name,
    string TargetPath,
    string? IconPath,
    string? Arguments,
    ShortcutLocation Location,
    string? WorkingDirectory = null,
    string? Description = null,
    bool PerUser = true);
