using System;
using System.Collections.Generic;
using Instella.Core.Manifest;

namespace Instella.Installer.Runtime.Builders;

/// <summary>
/// Fluent sub-builder for composing a <see cref="PayloadFilter"/>. Obtained
/// via <see cref="InstallerBuilder.WithPayloadFilter"/>. Authors chain
/// <see cref="Include"/>, <see cref="Exclude"/>, and <see cref="ExcludeDefaults"/>
/// calls in any order — each call appends to its respective list, and the
/// final lists are handed to the <c>AppendPayloadToSelf</c> MSBuild task via
/// the installer's manifest.
/// </summary>
/// <remarks>
/// Globs use the <c>Microsoft.Extensions.FileSystemGlobbing</c> syntax:
/// <c>**</c> matches any path segments (zero or more), <c>*</c> matches
/// within a single segment, and plain text matches literally. Forward
/// slashes are the separator regardless of host OS — the matcher treats
/// Windows backslashes transparently.
/// </remarks>
public sealed class PayloadFilterBuilder
{
    private readonly List<string> _include = new();
    private readonly List<string> _exclude = new();

    internal PayloadFilterBuilder()
    {
    }

    /// <summary>
    /// Add glob patterns that a payload file must match to be included.
    /// When the include list is empty the full payload set is included;
    /// once any pattern is added, only matching files survive
    /// (then <see cref="Exclude"/> prunes further).
    /// </summary>
    public PayloadFilterBuilder Include(params string[] globs)
    {
        ArgumentNullException.ThrowIfNull(globs);
        foreach (var g in globs)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(g);
            _include.Add(g);
        }
        return this;
    }

    /// <summary>
    /// Add glob patterns that prune files from the payload. Exclude wins
    /// over include on conflicts — a file matching both lists is dropped.
    /// </summary>
    public PayloadFilterBuilder Exclude(params string[] globs)
    {
        ArgumentNullException.ThrowIfNull(globs);
        foreach (var g in globs)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(g);
            _exclude.Add(g);
        }
        return this;
    }

    /// <summary>
    /// Expand a named <see cref="PayloadFilterPreset"/> into individual
    /// exclude globs and append them. Equivalent to passing the preset's
    /// glob list to <see cref="Exclude(string[])"/> verbatim — prefer the
    /// preset so a future runtime can tweak the glob list without every
    /// caller rewriting their installer.
    /// </summary>
    public PayloadFilterBuilder ExcludeDefaults(PayloadFilterPreset preset)
    {
        _exclude.AddRange(ExpandPreset(preset));
        return this;
    }

    /// <summary>
    /// Enumerates the concrete globs behind a preset. Exposed so tests and
    /// tooling can reason about the preset → glob mapping without
    /// instantiating a builder.
    /// </summary>
    public static IReadOnlyList<string> ExpandPreset(PayloadFilterPreset preset) => preset switch
    {
        PayloadFilterPreset.DebugArtifacts => new[]
        {
            "**/*.pdb",
            "**/*.xml",
            "**/*.deps.json",
            "**/*.runtimeconfig.dev.json",
        },
        _ => throw new ArgumentOutOfRangeException(
            nameof(preset), preset, $"Unknown {nameof(PayloadFilterPreset)} value."),
    };

    internal PayloadFilter Build() => new()
    {
        Include = _include.ToArray(),
        Exclude = _exclude.ToArray(),
    };

    internal bool HasAnyRules => _include.Count > 0 || _exclude.Count > 0;
}
