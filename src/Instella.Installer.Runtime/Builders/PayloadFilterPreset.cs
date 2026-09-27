namespace Instella.Installer.Runtime.Builders;

/// <summary>
/// Named bundles of glob patterns that <see cref="PayloadFilterBuilder.ExcludeDefaults"/>
/// expands into concrete exclude rules. Defined as an enum so authors can
/// flip on a curated set without re-typing the globs on every installer.
/// </summary>
public enum PayloadFilterPreset
{
    /// <summary>
    /// Build artifacts that should never ship in a production installer:
    /// Windows symbol files, XML documentation, dev host config, and the
    /// .NET runtime "deps" descriptor. Expands to:
    /// <list type="bullet">
    ///   <item><description><c>**/*.pdb</c></description></item>
    ///   <item><description><c>**/*.xml</c></description></item>
    ///   <item><description><c>**/*.deps.json</c></description></item>
    ///   <item><description><c>**/*.runtimeconfig.dev.json</c></description></item>
    /// </list>
    /// Leaves <c>*.runtimeconfig.json</c> (the non-dev variant) alone — it
    /// is required at runtime.
    /// </summary>
    DebugArtifacts,
}
