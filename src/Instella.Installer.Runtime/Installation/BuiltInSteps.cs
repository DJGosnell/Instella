using System;
using System.Collections.Generic;
using Instella.Core.Installation;
using Instella.Installer.Runtime.Builders;
using Instella.Installer.Runtime.Installation.BuiltIn;

namespace Instella.Installer.Runtime.Installation;

/// <summary>
/// The built-in steps of an install run: <see cref="OfflineInstallRunner.BuildDefaultSteps"/>, plus
/// the ones the configuration asks for (registry writes, Windows services), each placed in its
/// stage. <see cref="StepOrdering"/> keeps a list without user steps as it is, so the position in
/// this list is the position the step runs at.
/// </summary>
internal static class BuiltInSteps
{
    /// <summary>The built-in steps for <paramref name="config"/> over <paramref name="existing"/> (null: first install).</summary>
    public static List<IInstallStepExecution> For(FrozenConfig config, InstalledManifest? existing)
    {
        var steps = new List<IInstallStepExecution>(OfflineInstallRunner.BuildDefaultSteps());
        // After the structural Register steps, so the keys exist only once the files are in
        // place; before write-manifest, so a failed write still rolls back cleanly.
        if (config.RegistryWrites.Count > 0)
            Add(steps, new WriteRegistrySpecsStep(config.RegistryWrites));
        AddServiceSteps(steps, config, existing);
        return steps;
    }

    /// <summary>
    /// Whether the run needs the service steps: the installer declares services, or the
    /// installation it replaces recorded some (to stop, or to remove).
    /// </summary>
    public static bool NeedsServiceSteps(FrozenConfig config, InstalledManifest? existing) =>
        config.Services.Count > 0 || existing?.Services is { Count: > 0 };

    /// <summary>
    /// Adds <c>register-services</c> and <c>remove-dropped-services</c> at the end of the Register
    /// stage, <c>stop-services</c> right before <c>commit-transaction</c> and <c>start-services</c>
    /// right after <c>app-upgrade</c>, when <see cref="NeedsServiceSteps"/>.
    /// </summary>
    public static void AddServiceSteps(List<IInstallStepExecution> steps, FrozenConfig config, InstalledManifest? existing)
    {
        if (!NeedsServiceSteps(config, existing)) return;
        var run = new ServiceRun(config.Services);
        Add(steps, new RegisterServicesStep(run));
        Add(steps, new RemoveDroppedServicesStep(run));
        Add(steps, new StopServicesStep(run), before: CommitTransactionStep.StepName);
        Add(steps, new StartServicesStep(run), after: AppUpgradeStep.StepName);
    }

    /// <summary>
    /// Inserts <paramref name="step"/> right before or after the named built-in when given (and
    /// present), otherwise at the end of its stage: before the first step of a later stage.
    /// </summary>
    internal static void Add(List<IInstallStepExecution> steps, IInstallStepExecution step, string? before = null, string? after = null)
    {
        var anchor = before ?? after;
        if (anchor is not null)
        {
            var at = steps.FindIndex(s => string.Equals(s.Name, anchor, StringComparison.Ordinal));
            if (at >= 0)
            {
                steps.Insert(before is not null ? at : at + 1, step);
                return;
            }
        }
        var later = steps.FindIndex(s => s.Stage > step.Stage);
        if (later < 0) steps.Add(step);
        else steps.Insert(later, step);
    }
}
