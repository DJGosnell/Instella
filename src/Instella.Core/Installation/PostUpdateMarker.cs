using System.Text.Json;
using System.Text.Json.Serialization;
using Instella.Core.FileSystem;

namespace Instella.Core.Installation;

/// <summary>
/// The file the updater leaves in <c>{root}/.instella/post-update/</c> after a successful
/// update, so the restarted app learns "I was just updated from X" through the SDK. It replaces
/// command-line flags, which cannot survive the non-elevated relaunch through Explorer.
/// </summary>
/// <remarks>
/// The name is <c>{appId}.{updateId}.json</c>: unique per app and per update, so a stale file of
/// another app copied into the folder is never mistaken for this one. <see cref="MarkerVersion"/>
/// versions the format (docs/compatibility.md).
/// </remarks>
internal sealed record PostUpdateMarker
{
    /// <summary>The format version this code writes and reads.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Format version; readers skip any other value.</summary>
    public int MarkerVersion { get; init; } = CurrentVersion;

    /// <summary>A new <see cref="Guid"/> per update, formatted <c>"N"</c> (32 hex characters).</summary>
    public required string UpdateId { get; init; }

    /// <summary>The app that was updated.</summary>
    public required string AppId { get; init; }

    /// <summary>The version before the update.</summary>
    public required Version FromVersion { get; init; }

    /// <summary>The version after the update.</summary>
    public required Version ToVersion { get; init; }

    /// <summary>The channel of the release that was installed.</summary>
    public required string Channel { get; init; }

    /// <summary>When the update committed.</summary>
    public required DateTimeOffset CompletedAt { get; init; }

    /// <summary><c>UpdateOptions.AdditionalArgs</c>: the arguments the app asked to be restarted with.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];
}

/// <summary>Where post-update markers live, and how they are written atomically.</summary>
internal static class PostUpdateMarkers
{
    /// <summary>The marker folder, relative to the install root.</summary>
    public const string Directory = InstellaOwnedPaths.PostUpdateDirectory;

    /// <summary>The marker file name for one update of one app.</summary>
    public static string FileName(string appId, string updateId) => $"{appId}.{updateId}.json";

    /// <summary>
    /// Writes <paramref name="marker"/> to <c>{name}.tmp</c>, flushes it to disk, and renames it to
    /// <c>{name}.json</c> (atomic within one folder), so a reader never sees a half-written marker.
    /// Then deletes this app's older markers and stray <c>.tmp</c> files: only the newest update matters.
    /// </summary>
    public static async Task WriteAsync(IFileSystem fs, string installRoot, PostUpdateMarker marker, CancellationToken ct)
    {
        var folder = Path.Combine(installRoot, ".instella", "post-update");
        var created = await fs.CreateDirectoryAsync(folder, ct);
        if (!created.Success) throw new IOException($"could not create {folder}: {created.Error?.Message}");

        var name = FileName(marker.AppId, marker.UpdateId);
        var final = Path.Combine(folder, name);
        var temp = final[..^".json".Length] + ".tmp";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(marker, PostUpdateMarkerJsonContext.Default.PostUpdateMarker);

        var open = await fs.OpenWriteAsync(temp, ct);
        if (!open.Success || open.Value is null) throw new IOException($"could not write {temp}: {open.Error?.Message}");
        await using (var stream = open.Value)
        {
            await stream.WriteAsync(bytes, ct);
            await stream.FlushAsync(ct);
            if (stream is FileStream file) file.Flush(flushToDisk: true);
        }
        var moved = await fs.MoveFileAsync(temp, final, overwrite: true, ct);
        if (!moved.Success) throw new IOException($"could not rename {temp}: {moved.Error?.Message}");

        foreach (var path in fs.EnumerateFiles(folder))
        {
            var file = Path.GetFileName(path);
            var ownOlder = IsMarkerOf(file, marker.AppId) && !string.Equals(file, name, StringComparison.Ordinal);
            if (ownOlder || file.EndsWith(".tmp", StringComparison.Ordinal))
                await fs.DeleteFileAsync(path, ct);
        }
    }

    /// <summary>
    /// Whether <paramref name="fileName"/> is <c>{appId}.{32 hex}.json</c> for exactly this app id
    /// (app ids contain dots, so a prefix match would also take <c>{appId}.beta</c>'s markers).
    /// </summary>
    public static bool IsMarkerOf(string fileName, string appId)
    {
        if (!fileName.StartsWith(appId + ".", StringComparison.Ordinal) || !fileName.EndsWith(".json", StringComparison.Ordinal))
            return false;
        var id = fileName.AsSpan(appId.Length + 1, fileName.Length - appId.Length - 1 - ".json".Length);
        if (id.Length != 32) return false;
        foreach (var c in id)
            if (!char.IsAsciiHexDigitLower(c) && !char.IsAsciiDigit(c)) return false;
        return true;
    }

    /// <summary>Parses a marker; null when the bytes are not a marker this version reads.</summary>
    public static PostUpdateMarker? TryParse(byte[] bytes)
    {
        try
        {
            var marker = JsonSerializer.Deserialize(bytes, PostUpdateMarkerJsonContext.Default.PostUpdateMarker);
            return marker is { MarkerVersion: PostUpdateMarker.CurrentVersion } ? marker : null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
[JsonSerializable(typeof(PostUpdateMarker))]
internal partial class PostUpdateMarkerJsonContext : JsonSerializerContext;
