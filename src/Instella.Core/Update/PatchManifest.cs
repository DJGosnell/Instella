using System.Text.Json.Serialization;

namespace Instella.Core.Update;

/// <summary>
/// Manifest describing how to apply a patch update from one version to another.
/// Contains lists of patched files, new files, deleted files, and verification hashes.
/// </summary>
/// <remarks>
/// Internal: the server builds it and stores it verbatim, the client reads it. Readers treat a
/// missing <see cref="FormatVersion"/> as 1 and ignore a patch with a newer one (a full download
/// instead), so the format can grow (docs/compatibility.md, "Formats and reader rules").
/// </remarks>
internal sealed record PatchManifest
{
    /// <summary>The format this manifest is written in.</summary>
    public const int CurrentFormatVersion = 1;

    /// <summary>The format version; absent in the JSON means 1.</summary>
    /// <remarks>
    /// <c>set</c>, not <c>init</c> (like the optional lists below): the source-generated reader
    /// assigns every init-only property when it constructs the record, so an absent field would
    /// read as 0 (or null) instead of its default.
    /// </remarks>
    [JsonPropertyName("formatVersion")]
    public int FormatVersion { get; set; } = CurrentFormatVersion;

    /// <summary>The version being upgraded from.</summary>
    public required Version FromVersion { get; init; }

    /// <summary>The version being upgraded to.</summary>
    public required Version ToVersion { get; init; }

    /// <summary>Files that have binary diff patches to apply.</summary>
    public required IReadOnlyList<PatchedFile> PatchedFiles { get; init; }

    /// <summary>Files that are new and need to be downloaded in full (not read by the client).</summary>
    public IReadOnlyList<NewFile> NewFiles { get; set; } = [];

    /// <summary>Files that should be deleted (not read by the client: the signed release decides).</summary>
    public IReadOnlyList<string> DeletedFiles { get; set; } = [];

    /// <summary>Every file and its hash after the update (not read by the client: the signed release decides).</summary>
    public IReadOnlyList<FileHash> VerificationList { get; set; } = [];

    /// <summary>Total size of all patch downloads in bytes.</summary>
    public long TotalPatchSize => PatchedFiles.Sum(f => f.PatchSize);

    /// <summary>Total size of all new file downloads in bytes.</summary>
    public long TotalNewFilesSize => NewFiles.Sum(f => f.Size);

    /// <summary>Total download size (patches + new files).</summary>
    public long TotalDownloadSize => TotalPatchSize + TotalNewFilesSize;
}

/// <summary>
/// JSON serialization context for update types.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(UpdateInfo))]
[JsonSerializable(typeof(UpdateCheckResult))]
[JsonSerializable(typeof(PatchManifest))]
[JsonSerializable(typeof(PatchedFile))]
[JsonSerializable(typeof(NewFile))]
[JsonSerializable(typeof(FileHash))]
internal partial class UpdateJsonContext : JsonSerializerContext;
