using System;
using System.Collections.Generic;
using System.Linq;
using Instella.Core.FileSystem;
using Instella.Core.Installation;
using Instella.Core.Manifest;

namespace Instella.Installer.Runtime.Builders;

/// <summary>
/// What <see cref="InstallerBuilder.Build"/> checks about the services declared with
/// <see cref="WindowsBuilder.AddService"/>: SystemWide elevation, names, duplicates, dependency
/// cycles, executables and <see cref="ServiceBuilder.StartFailureIsWarning"/>.
/// </summary>
internal static class ServiceValidation
{
    /// <summary>The longest service name the service manager accepts.</summary>
    public const int MaxNameLength = 80;

    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> naming the service for the first problem
    /// found; returns the services with their executables normalised (<c>/</c> separators).
    /// </summary>
    public static IReadOnlyList<ServiceSpec> ValidateAndNormalize(IReadOnlyList<ServiceSpec> services, ElevationMode elevation)
    {
        if (services.Count == 0)
            return [];

        var result = new List<ServiceSpec>(services.Count);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var service in services)
        {
            if (elevation != ElevationMode.SystemWide)
                throw Refuse(service.Name, "a Windows service needs WithElevation(ElevationMode.SystemWide)");
            if (service.Name.Length is 0 or > MaxNameLength)
                throw Refuse(service.Name, $"a service name is 1 to {MaxNameLength} characters long");
            if (!service.Name.All(IsNameChar))
                throw Refuse(service.Name, "a service name may only contain letters, digits, '.', '_' and '-'");
            if (!names.Add(service.Name))
                throw new InvalidOperationException($"InstallerBuilder.Build(): duplicate service name '{service.Name}'.");

            var executable = service.Executable;
            if (executable is not null)
            {
                if (!SafePath.TryNormalizeRelative(executable, out var normalized, out var reason))
                    throw Refuse(service.Name, $"executable '{executable}' {reason}; it must be a path inside the install directory");
                if (InstellaOwnedPaths.IsOwned(normalized))
                    throw Refuse(service.Name, $"executable '{executable}' is a file Instella owns");
                executable = normalized;
            }

            if (service.StartFailureIsWarning && !service.StartsAfterInstall)
                throw Refuse(service.Name, "StartFailureIsWarning() needs a service that is started after install " +
                    "(an automatic one, or StartAfterInstall(true) on a manual one)");

            result.Add(service with { Executable = executable });
        }

        if (FindCycle(result) is { } cycle)
            throw new InvalidOperationException(
                $"InstallerBuilder.Build(): the services' DependsOn form a cycle ({string.Join(" → ", cycle)}).");

        return result;
    }

    private static bool IsNameChar(char c) => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '.' or '_' or '-';

    private static InvalidOperationException Refuse(string service, string problem) =>
        new($"InstallerBuilder.Build(): service '{service}': {problem}.");

    /// <summary>A dependency cycle among the declared services (first name repeated at the end), or null.</summary>
    private static IReadOnlyList<string>? FindCycle(IReadOnlyList<ServiceSpec> services)
    {
        var byName = services.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var path = new List<string>();

        IReadOnlyList<string>? Visit(ServiceSpec service)
        {
            var at = path.FindIndex(p => string.Equals(p, service.Name, StringComparison.OrdinalIgnoreCase));
            if (at >= 0)
                return [.. path.Skip(at), service.Name];
            if (done.Contains(service.Name))
                return null;
            path.Add(service.Name);
            foreach (var dependency in service.Dependencies)
            {
                if (byName.TryGetValue(dependency, out var next) && Visit(next) is { } cycle)
                    return cycle;
            }
            path.RemoveAt(path.Count - 1);
            done.Add(service.Name);
            return null;
        }

        foreach (var service in services)
        {
            if (Visit(service) is { } cycle)
                return cycle;
        }
        return null;
    }
}
