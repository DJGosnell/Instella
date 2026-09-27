namespace Instella.Core.Platform;

/// <summary>
/// Information needed to register a file type association.
/// </summary>
/// <param name="Extension">File extension including the dot (e.g., ".myf").</param>
/// <param name="Description">Human-readable description of the file type.</param>
/// <param name="AppId">Unique identifier for the application.</param>
/// <param name="ExecutablePath">Full path to the executable that handles this file type.</param>
/// <param name="IconPath">Optional path to the icon file for this file type.</param>
/// <param name="PerUser"><c>HKCU\Software\Classes</c> (true) or <c>HKLM\Software\Classes</c> (false).</param>
public sealed record FileAssociationInfo(
    string Extension,
    string Description,
    string AppId,
    string ExecutablePath,
    string? IconPath,
    bool PerUser = true);
