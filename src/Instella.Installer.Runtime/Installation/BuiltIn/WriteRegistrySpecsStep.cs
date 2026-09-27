using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.Installation;
using Instella.Core.Platform;
using Instella.Installer.Runtime.Builders;

namespace Instella.Installer.Runtime.Installation.BuiltIn;

/// <summary>
/// Applies a frozen list of <see cref="RegistryWriteSpec"/> values through
/// <see cref="IPlatformServices.WriteRegistryValueAsync"/>, tracking each
/// write on the ledger so tracked-rollback can reverse it on later install
/// failure. Runs in <see cref="InstallStage.Register"/>. Skipped cleanly
/// (no-op, no ledger entries) when the spec list is empty.
/// </summary>
internal sealed class WriteRegistrySpecsStep : IInstallStepExecution
{
    private readonly IReadOnlyList<RegistryWriteSpec> _specs;

    public WriteRegistrySpecsStep(IReadOnlyList<RegistryWriteSpec> specs)
    {
        ArgumentNullException.ThrowIfNull(specs);
        _specs = specs;
    }

    public string Name => "write-registry-specs";
    public InstallStage Stage => InstallStage.Register;

    public bool ShouldRun(InstallContext context) => _specs.Count > 0;

    public async Task<StepResult> ExecuteAsync(InstallContext context, IStepProgress progress, CancellationToken cancellationToken)
    {
        var perUser = context.Scope == InstallationScope.PerUser;
        var warnings = new List<string>();
        var keysSeen = new HashSet<(RegistryHive, string)>();

        for (var i = 0; i < _specs.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var spec = _specs[i];

            object value;
            try
            {
                value = spec.ValueFactory(context);
            }
            catch (Exception ex)
            {
                return StepResult.Fail($"registry value factory for {spec.KeyPath}\\{spec.ValueName} threw: {ex.Message}");
            }

            // A key that does not exist yet is created by this write: record it so uninstall
            // (and rollback) remove it again, but only keys Instella created.
            if (keysSeen.Add((spec.Hive, spec.KeyPath.ToLowerInvariant()))
                && !await context.Platform.RegistryKeyExistsAsync(spec.Hive, spec.KeyPath, perUser, cancellationToken))
            {
                context.TrackRegistryKey(spec.Hive, spec.KeyPath);
                context.ManifestRegistryEntries.Add(new ManifestRegistryEntry(
                    spec.Hive, spec.KeyPath, "", default, perUser, IsKey: true));
            }

            // Track BEFORE write so even a partial write is ledger-visible
            // (the value may partially exist if the Set* call aborts mid-
            // serialization). A value that already exists (an upgrade or repair,
            // or a value shared with something else) is restored, not deleted.
            var previous = await context.Platform.ReadRegistryValueAsync(
                spec.Hive, spec.KeyPath, spec.ValueName, perUser, cancellationToken);
            if (previous is not null)
                context.TrackRegistryValueRestore(spec.Hive, spec.KeyPath, spec.ValueName, previous);
            else
                context.TrackRegistryValue(spec.Hive, spec.KeyPath, spec.ValueName);

            var ok = await context.Platform.WriteRegistryValueAsync(
                spec.Hive, spec.KeyPath, spec.ValueName, spec.Kind, value, perUser, cancellationToken);

            if (ok.Success)
            {
                // Record for the v3 manifest's `registry` section so the
                // uninstaller can reverse each write. Only successful writes
                // land here; the ledger has the full list (including failures)
                // for rollback-on-install-failure.
                context.ManifestRegistryEntries.Add(new ManifestRegistryEntry(
                    spec.Hive, spec.KeyPath, spec.ValueName, spec.Kind, perUser));
            }
            else
                warnings.Add($"failed to write registry value {spec.KeyPath}\\{spec.ValueName}: {ok.Error}");

            progress.Report((i + 1) / (double)_specs.Count);
        }

        return warnings.Count > 0 ? StepResult.OkWithWarnings(warnings) : StepResult.Ok;
    }
}
