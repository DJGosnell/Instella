namespace Instella.Core.Manifest;

/// <summary>
/// Defines a file type association to register during installation.
/// </summary>
/// <param name="Extension">The file extension including the dot (e.g., ".myf").</param>
/// <param name="Description">Human-readable description of the file type.</param>
/// <param name="IconPath">Optional path to icon file for the file type.</param>
public sealed record FileAssociation(string Extension, string Description, string? IconPath);
