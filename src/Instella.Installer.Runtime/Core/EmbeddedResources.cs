using System.Text.Json;
using Instella.Core.Internal;
using Instella.Core.Manifest;

namespace Instella.Installer.Runtime.Core;

/// <summary>
/// Reads manifest, config, and archive data from the appended payload footer of the current EXE.
/// Uses Environment.ProcessPath instead of Assembly.Location for NativeAOT compatibility.
/// Delegates footer parsing and integrity verification to <see cref="PayloadFooterReader"/>.
/// </summary>
internal static class EmbeddedResources
{
    private static InstellaManifest? _cachedManifest;
    private static InstallerType? _cachedType;
    private static InstallerConfig? _cachedConfig;
    private static PayloadFooter? _cachedFooter;
    private static string? _payloadPathOverride;

    /// <summary>
    /// Test seam: read the payload from this file instead of the running executable. Setting
    /// it clears every cached read.
    /// </summary>
    internal static string? PayloadPathOverride
    {
        get => _payloadPathOverride;
        set
        {
            _payloadPathOverride = value;
            _cachedManifest = null;
            _cachedType = null;
            _cachedConfig = null;
            _cachedFooter = null;
        }
    }

    /// <summary>
    /// Loads the installation manifest from the appended payload.
    /// </summary>
    public static InstellaManifest LoadManifest()
    {
        if (_cachedManifest != null)
            return _cachedManifest;

        var footer = ReadFooter();
        if (!footer.IsValid)
            throw new InvalidOperationException("No valid Instella payload found in executable");

        var bytes = ReadSegment(footer.ManifestOffset, footer.ManifestLength);

        var manifest = JsonSerializer.Deserialize<InstellaManifest>(bytes, ManifestJsonContext.Default.InstellaManifest)
            ?? throw new InvalidOperationException("Failed to deserialize manifest");

        manifest.Validate();
        _cachedManifest = manifest;
        return manifest;
    }

    /// <summary>
    /// Detects the type of this installer (Offline or Lite).
    /// </summary>
    public static InstallerType GetInstallerType()
    {
        if (_cachedType.HasValue)
            return _cachedType.Value;

        var footer = ReadFooter();
        if (!footer.IsValid)
            throw new InvalidOperationException("No valid Instella payload found in executable");

        // archive_length > 0 means Offline
        if (footer.ArchiveLength > 0)
        {
            _cachedType = InstallerType.Offline;
            return InstallerType.Offline;
        }

        // config_length > 0 means Lite
        if (footer.ConfigLength > 0)
        {
            _cachedType = InstallerType.Lite;
            return InstallerType.Lite;
        }

        throw new InvalidOperationException("Invalid installer payload: no archive and no config");
    }

    /// <summary>
    /// Gets the configuration for lite installers.
    /// </summary>
    public static InstallerConfig? GetConfig()
    {
        if (_cachedConfig != null)
            return _cachedConfig;

        var footer = ReadFooter();
        if (!footer.IsValid || footer.ConfigLength == 0)
            return null;

        var configOffset = footer.ManifestOffset + footer.ManifestLength;
        var bytes = ReadSegment(configOffset, footer.ConfigLength);

        var config = JsonSerializer.Deserialize<InstallerConfig>(bytes, InstallerJsonContext.Default.InstallerConfig)
            ?? throw new InvalidOperationException("Failed to deserialize config");

        _cachedConfig = config;
        return config;
    }

    /// <summary>
    /// Opens the appended archive stream for offline installers.
    /// </summary>
    /// <returns>Stream positioned at the start of the archive, or null if not an offline installer.</returns>
    public static Stream? OpenAppendedArchive()
    {
        var footer = ReadFooter();
        if (!footer.IsValid || footer.ArchiveLength == 0)
            return null;

        var exePath = GetPayloadPath();

        var fileStream = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            fileStream.Seek(footer.ArchiveOffset, SeekOrigin.Begin);
            return new BoundedStream(fileStream, footer.ArchiveLength, ownsStream: true);
        }
        catch
        {
            fileStream.Dispose();
            return null;
        }
    }

    private static string GetPayloadPath()
    {
        if (_payloadPathOverride is { } overridden)
            return overridden;
#if PLATFORM_MACOS
        // macOS .app bundle: payload is in Contents/Resources/payload.instella
        var resourcePath = UI.MacOS.NS.BundleResourcePath();
        if (!string.IsNullOrEmpty(resourcePath))
        {
            var payloadPath = Path.Combine(resourcePath, "payload.instella");
            if (File.Exists(payloadPath))
                return payloadPath;
        }
        // Fallback for non-bundle execution (e.g. dev/debug)
        return Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot determine process path");
#else
        return Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot determine process path");
#endif
    }

    private static PayloadFooter ReadFooter()
    {
        if (_cachedFooter.HasValue)
            return _cachedFooter.Value;

        var footer = ReadVerifiedFooter(GetPayloadPath());
        _cachedFooter = footer;
        return footer;
    }

    /// <summary>
    /// Throws <see cref="FooterIntegrityException"/> when the running installer carries a
    /// footer that is malformed or whose SHA-256 does not match. A file with no footer at all
    /// (a lite installer) passes. Called before any install work, so a damaged installer
    /// never "heals" itself from a server.
    /// </summary>
    internal static void EnsurePayloadIntact() => ReadFooter();

    /// <summary>
    /// Reads and verifies the footer of <paramref name="path"/>. Returns a default
    /// (IsValid=false) footer when there is none; throws when one exists but is damaged.
    /// </summary>
    internal static PayloadFooter ReadVerifiedFooter(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return default;

        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return default; // cannot read ourselves: treated as "no embedded payload"
        }

        using (stream)
        {
            var payloadEnd = PayloadFooterReader.GetPayloadEndOffset(stream);
            var footer = PayloadFooterReader.ReadFooter(stream, payloadEnd);
            switch (footer.Status)
            {
                case FooterStatus.Missing:
                    return default;
                case FooterStatus.Malformed:
                    throw new FooterIntegrityException("The installer's payload footer is malformed; the file is damaged.");
                case FooterStatus.NewerFormat:
                    throw new FooterIntegrityException(
                        "This installer was built by a newer Instella and cannot be read by this version.");
            }

            PayloadFooterReader.VerifyIntegrity(stream, footer);
            return footer;
        }
    }

    private static byte[] ReadSegment(long offset, int length)
    {
        var exePath = GetPayloadPath();

        using var stream = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        stream.Seek(offset, SeekOrigin.Begin);

        var buffer = new byte[length];
        var totalRead = 0;
        while (totalRead < length)
        {
            var read = stream.Read(buffer, totalRead, length - totalRead);
            if (read == 0)
                throw new InvalidOperationException($"Unexpected end of file reading segment at offset {offset}");
            totalRead += read;
        }

        return buffer;
    }

    /// <summary>
    /// Stream that limits reading to a specified length.
    /// </summary>
    internal sealed class BoundedStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _length;
        private readonly bool _ownsStream;
        private long _position;

        public BoundedStream(Stream inner, long length, bool ownsStream)
        {
            if (!inner.CanSeek)
                throw new ArgumentException("BoundedStream requires a seekable inner stream.", nameof(inner));
            _inner = inner;
            _length = length;
            _ownsStream = ownsStream;
        }

        public override bool CanRead => true;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => _length;

        public override long Position
        {
            get => _position;
            set
            {
                if (value < 0 || value > _length)
                    throw new ArgumentOutOfRangeException(nameof(value));
                _inner.Position = _inner.Position - _position + value;
                _position = value;
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var remaining = _length - _position;
            if (remaining <= 0) return 0;

            var toRead = (int)Math.Min(count, remaining);
            var read = _inner.Read(buffer, offset, toRead);
            _position += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            var newPosition = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => _length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };

            Position = newPosition;
            return _position;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && _ownsStream)
            {
                _inner.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}

/// <summary>
/// JSON serialization context for installer types.
/// </summary>
[System.Text.Json.Serialization.JsonSerializable(typeof(InstallerConfig))]
internal partial class InstallerJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
