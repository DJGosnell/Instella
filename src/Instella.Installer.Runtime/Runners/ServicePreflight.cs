using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.FileSystem;
using Instella.Core.Installation;
using Instella.Installer.Runtime.Builders;
using Instella.Installer.Runtime.Core.Security;
using Instella.Installer.Runtime.Installation;
using Instella.Installer.Runtime.Installation.BuiltIn;

namespace Instella.Installer.Runtime.Runners;

/// <summary>
/// The checks a service install passes before anything is created, staged or committed; each
/// failure is an <see cref="InstallRefusedException"/> with exit 10. A declared service's
/// executable must be in the payload; the install folder must be one only administrators can
/// change; and a service of the same name that this installation does not record is never taken over.
/// </summary>
internal static class ServicePreflight
{
    /// <summary>
    /// Gives <paramref name="context"/> its service manager when the run needs the service steps,
    /// then, when the installer declares services, runs the checks.
    /// </summary>
    public static async Task PrepareAsync(FrozenConfig config, InstallContext context, InstallerServices? services, CancellationToken ct)
    {
        if (!BuiltInSteps.NeedsServiceSteps(config, context.ExistingInstallation)) return;
        context.ServiceManager ??= services?.ServiceManagerOrDefault ?? Instella.Core.Platform.ServiceManagerFactory.Create();
        if (config.Services.Count == 0) return;

        var run = new ServiceRun(config.Services);
        if (PayloadEntries(context.PayloadArchive) is { } entries)
        {
            foreach (var spec in run.Declared)
            {
                var rel = ServiceRun.RelativeExecutable(spec, context);
                if (!entries.Contains(rel))
                    Refuse($"service '{spec.Name}': executable '{rel}' is not in the payload");
            }
        }

        var acl = services?.FolderAclOrDefault ?? InstallFolderAcl.Host;
        if (acl.FindNonAdminWriter(context.InstallPath) is { } writer)
            Refuse($"'{context.InstallPath}' can be written by {writer}; a service must be installed in a folder " +
                   "only administrators can change (for example Program Files)");

        foreach (var spec in run.Declared)
        {
            var found = await context.ServiceManager.QueryConfigurationAsync(spec.Name, ct);
            if (found.Outcome.Success && ServiceRun.Recorded(context, spec.Name) is null)
                Refuse($"a service named '{spec.Name}' already exists and is not part of this installation");
        }
    }

    private static void Refuse(string message) =>
        throw new InstallRefusedException(InstellaExitCode.InstallGeneralFailure, message);

    /// <summary>The payload's file entries ('/' separators), or null when the archive cannot be listed without consuming it.</summary>
    private static HashSet<string>? PayloadEntries(Stream? payload)
    {
        if (payload is null || !payload.CanSeek) return null;
        var start = payload.Position;
        try
        {
            using var zip = new ZipArchive(payload, ZipArchiveMode.Read, leaveOpen: true);
            return zip.Entries
                .Where(e => !string.IsNullOrEmpty(e.Name))
                .Select(e => SafePath.TryNormalizeRelative(e.FullName, out var rel, out _) ? rel : e.FullName.Replace('\\', '/'))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (InvalidDataException)
        {
            return null; // extract-payload reports a damaged archive
        }
        finally
        {
            payload.Position = start;
        }
    }
}
