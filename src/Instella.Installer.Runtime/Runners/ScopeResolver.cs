using System;
using Instella.Core.Installation;
using Instella.Core.Manifest;

namespace Instella.Installer.Runtime.Runners;

/// <summary>What to do about scope before an install starts.</summary>
internal enum ScopeAction
{
    /// <summary>Run in <see cref="ScopeDecision.Scope"/> in this process.</summary>
    Proceed,

    /// <summary>A machine install in an unelevated interactive run: relaunch through UAC.</summary>
    RelaunchElevated,

    /// <summary>A machine install in an unelevated silent run: exit 51 (silent never pops UAC).</summary>
    RefuseNotElevated,

    /// <summary><see cref="ElevationMode.UserChoice"/> interactively: show the scope page first.</summary>
    AskUser,

    /// <summary><c>--scope</c> has a value other than user or machine: exit 40.</summary>
    InvalidScopeArgument,
}

/// <param name="Action">What to do next.</param>
/// <param name="Scope">The scope to install in; null while it is still to be decided.</param>
internal readonly record struct ScopeDecision(ScopeAction Action, InstallationScope? Scope = null);

/// <summary>The scope-resolution table.</summary>
internal static class ScopeResolver
{
    /// <param name="mode">How the builder configured elevation.</param>
    /// <param name="silent"><c>--silent</c>.</param>
    /// <param name="scopeArgument">The value of <c>--scope</c>, if given.</param>
    /// <param name="isElevated">The process already has administrator rights.</param>
    /// <param name="existingScope">
    /// The scope of the installation this run upgrades or repairs in place: it wins over the
    /// builder's mode and the scope page, so a re-install never creates a second copy.
    /// </param>
    public static ScopeDecision Resolve(ElevationMode mode, bool silent, string? scopeArgument, bool isElevated,
        InstallationScope? existingScope = null)
    {
        InstallationScope? requested = scopeArgument?.ToLowerInvariant() switch
        {
            null => null,
            "user" => InstallationScope.PerUser,
            "machine" => InstallationScope.SystemWide,
            _ => (InstallationScope)(-1),
        };
        if (requested is { } r && !Enum.IsDefined(r))
            return new ScopeDecision(ScopeAction.InvalidScopeArgument);

        if (existingScope is { } existing)
            return existing == InstallationScope.PerUser
                ? new ScopeDecision(ScopeAction.Proceed, InstallationScope.PerUser)
                : Machine(silent, isElevated);

        return mode switch
        {
            ElevationMode.PerUser => new ScopeDecision(ScopeAction.Proceed, InstallationScope.PerUser),
            ElevationMode.SystemWide => Machine(silent, isElevated),
            _ => requested switch
            {
                InstallationScope.SystemWide => Machine(silent, isElevated),
                InstallationScope.PerUser => new ScopeDecision(ScopeAction.Proceed, InstallationScope.PerUser),
                _ => silent
                    ? new ScopeDecision(ScopeAction.Proceed, InstallationScope.PerUser)
                    : new ScopeDecision(ScopeAction.AskUser),
            },
        };
    }

    private static ScopeDecision Machine(bool silent, bool isElevated) =>
        isElevated ? new ScopeDecision(ScopeAction.Proceed, InstallationScope.SystemWide)
        : silent ? new ScopeDecision(ScopeAction.RefuseNotElevated)
        : new ScopeDecision(ScopeAction.RelaunchElevated);
}
