namespace Instella.Core.Installation;

/// <summary>
/// Ordered stages the step pipeline passes through. Custom steps slot
/// relative to a stage via <c>before:</c> / <c>after:</c>.
/// </summary>
public enum InstallStage
{
    /// <summary>Prerequisites are detected and installed.</summary>
    Prereqs,
    /// <summary>Application files are staged (not yet live).</summary>
    Extract,
    /// <summary>Shortcuts, file associations, PATH, registry and the Installed Apps entry are registered.</summary>
    Register,
    /// <summary>The install transaction commits; steps here run after the new files are live.</summary>
    Finalize,
}
