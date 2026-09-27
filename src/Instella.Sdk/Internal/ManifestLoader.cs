using System.Text.Json;
using Instella.Core.Installation;

namespace Instella.Sdk.Internal;

/// <summary>
/// Finds and reads the installed manifest: from <see cref="AppContext.BaseDirectory"/>
/// it walks up at most <see cref="MaxLevelsUp"/> levels looking for
/// <c>.instella-manifest.json</c>, so an app in a payload subdirectory still finds its root.
/// </summary>
internal static class ManifestLoader
{
    internal const int MaxLevelsUp = 3;

    /// <summary>The installation containing <paramref name="startDirectory"/>, or null with the reason.</summary>
    public static InstellaInfo? TryLoad(string startDirectory, out string? reason)
    {
        var root = FindInstallRoot(startDirectory);
        if (root is null)
        {
            reason = $"no {InstellaOwnedPaths.InstalledManifest} in '{startDirectory}' or up to {MaxLevelsUp} parent directories; " +
                     "the app was not installed by Instella";
            return null;
        }

        InstalledManifest? manifest;
        try
        {
            var bytes = File.ReadAllBytes(Path.Combine(root, InstellaOwnedPaths.InstalledManifest));
            manifest = JsonSerializer.Deserialize(bytes, InstalledManifestJsonContext.Default.InstalledManifest);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            reason = $"the installed manifest in '{root}' cannot be read: {ex.Message}";
            return null;
        }

        if (manifest is null || manifest.ManifestVersion != InstallManifestWriter.CurrentVersion)
        {
            reason = $"the installed manifest in '{root}' has an unsupported format";
            return null;
        }

        reason = null;
        return new InstellaInfo
        {
            AppName = manifest.AppName,
            AppId = manifest.AppId,
            Version = manifest.Version,
            InstallRoot = root,
            Channel = string.IsNullOrEmpty(manifest.Channel) ? "stable" : manifest.Channel,
            Architecture = manifest.Architecture,
            IsPerUser = manifest.InstalledPerUser,
            ServerUrl = manifest.ServerUrl,
            ExecutableName = manifest.ExecutableName,
            Platform = manifest.Platform,
            TrustedKeys = manifest.TrustedKeys ?? [],
            AllowUnsignedUpdates = manifest.AllowUnsignedUpdates,
            AllowInsecureServer = manifest.AllowInsecureServer,
            DownloadToken = manifest.DownloadToken,
        };
    }

    /// <summary>The nearest directory at or above <paramref name="startDirectory"/> holding an installed manifest.</summary>
    public static string? FindInstallRoot(string startDirectory)
    {
        var dir = new DirectoryInfo(Path.GetFullPath(startDirectory));
        for (var level = 0; dir is not null && level <= MaxLevelsUp; level++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, InstellaOwnedPaths.InstalledManifest)))
                return dir.FullName;
        }
        return null;
    }
}
