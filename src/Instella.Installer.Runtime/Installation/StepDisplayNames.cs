using System.Collections.Generic;
using Instella.Core.Installation;
using Instella.Installer.Runtime.Installation.BuiltIn;

namespace Instella.Installer.Runtime.Installation;

/// <summary>
/// The names the wizard shows for steps: readable text for the built-in steps, the
/// <c>StepBuilder.WithDisplayName</c> text for a user step, else the step's name.
/// </summary>
internal static class StepDisplayNames
{
    private static readonly Dictionary<string, string> BuiltIn = new()
    {
        [PrerequisitesStep.StepName] = "Checking prerequisites",
        ["extract-payload"] = "Copying files",
        ["create-shortcuts"] = "Creating shortcuts",
        ["register-file-associations"] = "Registering file types",
        ["add-to-path"] = "Adding to PATH",
        ["configure-auto-start"] = "Setting up start with Windows",
        ["remove-dropped-integrations"] = "Removing integrations no longer used",
        ["stage-uninstaller-stub"] = "Preparing the uninstaller",
        ["register-uninstall-entry"] = "Registering with Windows",
        ["write-manifest"] = "Saving installation details",
        ["write-registry-specs"] = "Writing registry settings",
        [CommitTransactionStep.StepName] = "Moving files into place",
    };

    /// <summary>The display name of <paramref name="step"/>.</summary>
    public static string For(IInstallStep step) =>
        step is Builders.StepSpec { DisplayName: { } own } ? own
        : BuiltIn.TryGetValue(step.Name, out var builtIn) ? builtIn
        : step.Name;

    /// <summary>Step name to display name, for everything in <paramref name="steps"/>.</summary>
    public static IReadOnlyDictionary<string, string> Map(IEnumerable<IInstallStep> steps)
    {
        var map = new Dictionary<string, string>(System.StringComparer.Ordinal);
        foreach (var step in steps) map[step.Name] = For(step);
        return map;
    }
}
