namespace Instella.Core.Manifest;

/// <summary>
/// Glob-based include/exclude lists that the <c>AppendPayloadToSelf</c>
/// MSBuild task applies when building the payload ZIP. Both lists match
/// against each file's zip-entry (target) path — forward-slashed and
/// relative to the archive root — and follow the
/// <c>Microsoft.Extensions.FileSystemGlobbing</c> syntax (<c>**</c> for
/// recursive, <c>*</c> for single-segment, plain literals otherwise).
/// Matching is case-insensitive on Windows build hosts and case-sensitive
/// elsewhere.
/// </summary>
/// <remarks>
/// Include/exclude semantics mirror the globbing library: if
/// <see cref="Include"/> is empty, every file is included by default
/// and <see cref="Exclude"/> prunes; if <see cref="Include"/> has
/// entries, only matching files are considered before
/// <see cref="Exclude"/> prunes further. Authors who want "all files
/// minus some patterns" supply only <see cref="Exclude"/>; authors who
/// want "only these patterns minus these overrides" supply both.
/// </remarks>
public sealed record PayloadFilter
{
    /// <summary>
    /// Globs that a payload file must match to be included. Empty means
    /// every file is included (subject to <see cref="Exclude"/>).
    /// </summary>
    public IReadOnlyList<string> Include { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Globs that prune files from the payload. Applied after
    /// <see cref="Include"/>; exclude always wins over include on
    /// conflicting globs.
    /// </summary>
    public IReadOnlyList<string> Exclude { get; init; } = Array.Empty<string>();
}
