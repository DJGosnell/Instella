namespace Instella.Core.Manifest;

/// <summary>
/// Specifies how the installer should handle elevation (admin privileges).
/// </summary>
public enum ElevationMode : byte
{
    /// <summary>Install for current user only, no admin required.</summary>
    PerUser = 0,

    /// <summary>Install system-wide, requires admin privileges.</summary>
    SystemWide = 1,

    /// <summary>Let user choose during installation.</summary>
    UserChoice = 2
}
