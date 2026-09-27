using System;
using System.Linq;
using Instella.Core.Manifest;

namespace Instella.Installer.Runtime.Builders;

/// <summary>
/// Fluent configuration for a single <see cref="Prerequisite"/>. Obtained via
/// <see cref="InstallerBuilder.WithPrerequisite"/>; each invocation produces
/// one prereq entry. <see cref="WithName"/> is required, and a
/// <see cref="WithDownloadUrl"/> must be paired with <see cref="WithSha256"/>.
/// </summary>
public sealed class PrerequisiteBuilder
{
    private string? _name;
    private string? _bundlePath;
    private string? _downloadUrl;
    private string? _sha256;
    private string? _detectionRegistry;
    private string[]? _installArguments;
    private int[]? _successExitCodes;

    internal PrerequisiteBuilder() { }

    /// <summary>Required. The prerequisite name shown to the user and in the log.</summary>
    public PrerequisiteBuilder WithName(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        _name = name;
        return this;
    }

    /// <summary>Payload-relative path of a bundled installer (under <c>.instella/prereqs/</c>).</summary>
    public PrerequisiteBuilder WithBundlePath(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        _bundlePath = path;
        return this;
    }

    /// <summary>Download URL. Requires <see cref="WithSha256"/>.</summary>
    public PrerequisiteBuilder WithDownloadUrl(string url)
    {
        ArgumentException.ThrowIfNullOrEmpty(url);
        _downloadUrl = url;
        return this;
    }

    /// <summary>SHA-256 (hex) the downloaded installer must match before it is executed.</summary>
    public PrerequisiteBuilder WithSha256(string hex)
    {
        ArgumentException.ThrowIfNullOrEmpty(hex);
        var normalized = hex.Trim().ToLowerInvariant();
        if (normalized.Length != 64 || !normalized.All(char.IsAsciiHexDigitLower))
            throw new ArgumentException("WithSha256 expects 64 hex characters.", nameof(hex));
        _sha256 = normalized;
        return this;
    }

    /// <summary>A registry key whose presence means the prerequisite is already installed.</summary>
    public PrerequisiteBuilder WithDetectionRegistry(string keyPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(keyPath);
        _detectionRegistry = keyPath;
        return this;
    }

    /// <summary>Arguments for the installer; replaces the default <c>/quiet /norestart</c>.</summary>
    public PrerequisiteBuilder WithInstallArguments(params string[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        _installArguments = arguments;
        return this;
    }

    /// <summary>Exit codes treated as success; replaces the default <c>0, 3010</c>.</summary>
    public PrerequisiteBuilder WithSuccessExitCodes(params int[] exitCodes)
    {
        ArgumentNullException.ThrowIfNull(exitCodes);
        if (exitCodes.Length == 0)
            throw new ArgumentException("At least one success exit code is required.", nameof(exitCodes));
        _successExitCodes = exitCodes;
        return this;
    }

    internal Prerequisite Build()
    {
        if (string.IsNullOrEmpty(_name))
            throw new InvalidOperationException("Prerequisite requires WithName(...)");
        if (_downloadUrl is not null && _sha256 is null)
            throw new InvalidOperationException(
                $"Prerequisite '{_name}': WithDownloadUrl(...) requires WithSha256(...); a downloaded installer is only executed when its hash matches.");

        var prerequisite = new Prerequisite
        {
            Name = _name,
            BundlePath = _bundlePath,
            DownloadUrl = _downloadUrl,
            Sha256 = _sha256,
            DetectionRegistry = _detectionRegistry,
        };
        // Only override the defaults when the author set them.
        if (_installArguments is not null) prerequisite = With(prerequisite, args: _installArguments);
        if (_successExitCodes is not null) prerequisite = With(prerequisite, codes: _successExitCodes);
        return prerequisite;
    }

    private static Prerequisite With(Prerequisite p, string[]? args = null, int[]? codes = null) => new()
    {
        Name = p.Name,
        BundlePath = p.BundlePath,
        DownloadUrl = p.DownloadUrl,
        Sha256 = p.Sha256,
        DetectionRegistry = p.DetectionRegistry,
        RequiresElevation = p.RequiresElevation,
        InstallArguments = args ?? p.InstallArguments,
        SuccessExitCodes = codes ?? p.SuccessExitCodes,
    };
}
