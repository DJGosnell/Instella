using System;

namespace Instella.Installer.Runtime.Builders;

/// <summary>
/// Frozen user-declared CLI flag. Produced by
/// <see cref="InstallerBuilder.AddCliFlag{T}"/>; consumed by the
/// <c>CliArgParser</c> at <c>RunAsync</c> time.
/// </summary>
internal sealed record CliFlagSpec(string Name, Type ValueType, object? DefaultValue, string? Help, string? MapsTo);

/// <summary>Types the CLI parser can coerce a raw argument string to.</summary>
internal static class SupportedCliFlagTypes
{
    public static readonly Type[] All =
    {
        typeof(bool), typeof(int), typeof(long), typeof(string), typeof(string[]),
        typeof(System.IO.FileInfo), typeof(System.IO.DirectoryInfo),
    };

    public static bool IsSupported(Type t) => Array.IndexOf(All, t) >= 0;
}
