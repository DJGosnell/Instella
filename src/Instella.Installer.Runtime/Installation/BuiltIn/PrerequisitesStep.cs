using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.FileSystem;
using Instella.Core.Installation;
using Instella.Core.Manifest;
using Instella.Installer.Runtime.Core;

namespace Instella.Installer.Runtime.Installation.BuiltIn;

/// <summary>
/// Runs registered prerequisites (e.g., VC++ runtime). No rollback — per the
/// design decision, prereqs may now be shared across installs and are not
/// unwound on failure.
/// </summary>
internal sealed class PrerequisitesStep : IInstallStepExecution
{
    /// <summary>Payload folder that holds bundled prerequisite installers.</summary>
    internal const string BundleFolder = ".instella/prereqs/";

    /// <summary>Step name; a failure of this step exits <see cref="InstellaExitCode.InstallPrereqFailed"/>.</summary>
    internal const string StepName = "prerequisites";

    public string Name => StepName;
    public InstallStage Stage => InstallStage.Prereqs;
    public int Weight => 2;

    /// <summary>Test seam; null uses the real installer.</summary>
    internal Func<InstallContext, PrerequisiteInstaller>? InstallerFactory { get; init; }

    public async Task<StepResult> ExecuteAsync(InstallContext context, IStepProgress progress, CancellationToken cancellationToken)
    {
        if (context.Manifest.Prerequisites is not { Count: > 0 })
        {
            progress.Report(1.0);
            return StepResult.Ok;
        }

        var installer = InstallerFactory?.Invoke(context)
            ?? new PrerequisiteInstaller(context.Manifest.Prerequisites, context.FileSystem)
            {
                AllowElevationPrompt = context.AllowElevationPrompt,
            };
        var missing = await installer.GetMissingAsync(cancellationToken);

        if (missing.Count == 0)
        {
            context.Log.Debug("prerequisites: all already installed");
            progress.Report(1.0);
            return StepResult.Ok;
        }

        var completed = 0;
        foreach (var prereq in missing)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Registry detection only exists on Windows; elsewhere such a prerequisite
            // would always look missing and be reinstalled on every run.
            if (!OperatingSystem.IsWindows() && prereq.DetectionRegistry is not null)
            {
                context.Log.Warn($"prerequisites: skipping '{prereq.Name}': registry detection is Windows-only");
                completed++;
                continue;
            }

            context.Log.Info($"prerequisites: installing {prereq.Name}");
            progress.Report(completed / (double)missing.Count, $"Installing {prereq.Name}");
            try
            {
                await using var bundled = OpenBundled(context, prereq);
                var result = await installer.InstallAsync(prereq, bundled, cancellationToken);
                if (result.RebootRequired)
                {
                    context.RebootRequired = true;
                    context.Log.Info($"prerequisites: '{prereq.Name}' requires a reboot");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return StepResult.Fail($"prerequisite '{prereq.Name}' failed: {ex.Message}");
            }
            completed++;
        }

        progress.Report(1.0);
        return StepResult.Ok;
    }

    /// <summary>
    /// Copies a bundled installer out of the (integrity-verified) payload archive, or
    /// returns null when the prerequisite is not bundled.
    /// </summary>
    private static Stream? OpenBundled(InstallContext context, Prerequisite prereq)
    {
        if (string.IsNullOrEmpty(prereq.BundlePath) || context.PayloadArchive is null)
            return null;

        if (!SafePath.TryNormalizeRelative(prereq.BundlePath, out var relative, out var why))
            throw new UnsafePathException(prereq.BundlePath, why);
        var entryName = relative.StartsWith(BundleFolder, StringComparison.OrdinalIgnoreCase) ? relative : BundleFolder + relative;

        var start = context.PayloadArchive.Position;
        try
        {
            using var zip = new ZipArchive(context.PayloadArchive, ZipArchiveMode.Read, leaveOpen: true);
            var entry = zip.GetEntry(entryName)
                ?? throw new InvalidOperationException($"bundled prerequisite '{entryName}' is not in the payload");
            var copy = new MemoryStream();
            using (var s = entry.Open()) s.CopyTo(copy);
            copy.Position = 0;
            return copy;
        }
        finally
        {
            context.PayloadArchive.Position = start;
        }
    }
}
