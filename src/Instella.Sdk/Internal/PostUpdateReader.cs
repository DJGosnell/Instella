using Instella.Core.Installation;

namespace Instella.Sdk.Internal;

/// <summary>
/// Finds the post-update marker the updater left for this app and reports it once per user.
/// </summary>
/// <remarks>
/// <para>The marker lives in the install folder, which a machine-wide install's users cannot
/// write; which markers a user has seen is recorded in that user's own profile
/// (<c>%LOCALAPPDATA%\Instella\{appId}\last-post-update</c>), so each user's first start after an
/// update reports it once, per-user and machine-wide alike.</para>
/// <para>A marker older than <see cref="MaxAge"/> is ignored. Known edge: a user who first starts
/// the app within that time of an update also sees it once.</para>
/// </remarks>
internal static class PostUpdateReader
{
    /// <summary>Markers older than this are stale.</summary>
    internal static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);

    private const string SeenFileName = "last-post-update";

    /// <summary>Test seam: the per-user folder that holds the seen-records (one folder per app id).</summary>
    internal static Func<string> SeenStoreRoot { get; set; } = () =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Instella");

    /// <summary>
    /// The newest unseen marker of <paramref name="appId"/> in <paramref name="installRoot"/>,
    /// recorded as seen before it is returned; null when there is none.
    /// </summary>
    public static PostUpdateInfo? Read(string installRoot, string appId, DateTimeOffset now)
    {
        var folder = Path.Combine(installRoot, ".instella", "post-update");
        PostUpdateMarker? newest = null;
        try
        {
            if (!Directory.Exists(folder)) return null;
            foreach (var path in Directory.EnumerateFiles(folder, "*.json"))
            {
                if (!PostUpdateMarkers.IsMarkerOf(Path.GetFileName(path), appId)) continue;
                PostUpdateMarker? marker;
                try
                {
                    marker = PostUpdateMarkers.TryParse(File.ReadAllBytes(path));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }
                if (marker is null || !string.Equals(marker.AppId, appId, StringComparison.Ordinal)) continue;
                if (newest is null || marker.CompletedAt > newest.CompletedAt) newest = marker;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (newest is null || now - newest.CompletedAt > MaxAge) return null;

        var seenPath = Path.Combine(SeenStoreRoot(), SafeName(appId), SeenFileName);
        try
        {
            if (File.Exists(seenPath) && string.Equals(File.ReadAllText(seenPath).Trim(), newest.UpdateId, StringComparison.Ordinal))
                return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable: report, the worst case is hearing about the update again.
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(seenPath)!);
            var temp = seenPath + ".tmp";
            File.WriteAllText(temp, newest.UpdateId);
            File.Move(temp, seenPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still report: at worst the app hears about the same update on its next start.
        }

        return new PostUpdateInfo(newest.FromVersion, newest.ToVersion, newest.Channel, newest.CompletedAt, newest.Arguments);
    }

    /// <summary><paramref name="appId"/> with characters that are invalid in file names replaced by '_'.</summary>
    internal static string SafeName(string appId)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(appId.Select(c => invalid.Contains(c) ? '_' : c));
    }
}
