using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.FileSystem;
using Instella.Core.Logging;
using Instella.Core.Platform;
using Instella.Installer.Runtime.Core;
using NUnit.Framework;
using Instella.Core.Installation;

namespace Instella.Installer.Runtime.Tests.Installation;

/// <summary>
/// Installed manifest: round-trip with registry/cli/logging sections, and the version guard
/// (this runtime reads exactly version 4; any other version is rejected rather than guessed at).
/// </summary>
[TestFixture]
public sealed class ManifestV3Tests
{
    [Test]
    public async Task RoundTrip_retainsAllV3Fields()
    {
        var manifest = new InstalledManifest
        {
            AppName = "Test",
            AppId = "com.test.app",
            Version = new Version(1, 2, 3),
            InstallDirectory = "C:\\apps\\test",
            ExecutableName = "Test.exe",
            InstalledAt = new DateTime(2026, 4, 17, 12, 0, 0, DateTimeKind.Utc),
            HasUninstallEntry = true,
            InstalledPerUser = true,
            Platform = TargetPlatform.Windows,
            Files = Array.Empty<InstalledFile>(),
            Registry = new[]
            {
                new ManifestRegistryEntry(RegistryHive.CurrentUser, "Software\\Test", "InstallPath", InstellaRegistryValueKind.String, PerUser: true),
            },
            DeclaredCliFlags = new[]
            {
                new ManifestCliFlag("--license-key", "String", "license.key"),
            },
            Logging = new ManifestLoggingConfig(InstellaLogLevel.Info, "%TEMP%\\test.log", RetainCount: 10),
        };

        var fs = new SimpleFs();
        var writer = new InstallManifestWriter(fs);
        await writer.WriteAsync("C:\\test-out", manifest, CancellationToken.None);

        var readBack = await writer.ReadAsync("C:\\test-out", CancellationToken.None);
        Assert.That(readBack, Is.Not.Null);
        Assert.That(readBack!.ManifestVersion, Is.EqualTo(4));
        Assert.That(readBack.Registry, Is.Not.Null);
        Assert.That(readBack.Registry![0].KeyPath, Is.EqualTo("Software\\Test"));
        Assert.That(readBack.DeclaredCliFlags![0].MapsTo, Is.EqualTo("license.key"));
        Assert.That(readBack.Logging!.DefaultLevel, Is.EqualTo(InstellaLogLevel.Info));
    }

    [Test]
    public async Task Read_rejectsManifestWithoutVersion3()
    {
        // Construct a v2-style JSON blob without manifestVersion field.
        var v2Json = """
            {
              "appName": "Old",
              "appId": "com.old",
              "version": "1.0.0",
              "installDirectory": "C:\\old",
              "executableName": "Old.exe",
              "installedAt": "2024-01-01T00:00:00Z",
              "installedPerUser": true,
              "files": []
            }
            """;

        var fs = new SimpleFs();
        var path = "C:\\legacy\\.instella-manifest.json";
        fs.Files[path] = Encoding.UTF8.GetBytes(v2Json);

        var writer = new InstallManifestWriter(fs);
        var result = await writer.ReadAsync("C:\\legacy", CancellationToken.None);
        Assert.That(result, Is.Null);
    }

    [TestCase(2)]
    [TestCase(3)]   // an earlier format
    [TestCase(5)]   // a later Instella
    public async Task Read_rejectsManifestOfAnotherVersion(int version)
    {
        var v2Json = $$"""
            {
              "manifestVersion": {{version}},
              "appName": "Old",
              "appId": "com.old",
              "version": "1.0.0",
              "installDirectory": "C:\\old",
              "executableName": "Old.exe",
              "installedAt": "2024-01-01T00:00:00Z",
              "files": []
            }
            """;
        var fs = new SimpleFs();
        fs.Files["C:\\legacy\\.instella-manifest.json"] = Encoding.UTF8.GetBytes(v2Json);
        var writer = new InstallManifestWriter(fs);
        var result = await writer.ReadAsync("C:\\legacy", CancellationToken.None);
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task Read_returnsNullOnMalformedJson()
    {
        var fs = new SimpleFs();
        fs.Files["C:\\corrupt\\.instella-manifest.json"] = Encoding.UTF8.GetBytes("not json at all");
        var writer = new InstallManifestWriter(fs);
        var result = await writer.ReadAsync("C:\\corrupt", CancellationToken.None);
        Assert.That(result, Is.Null);
    }

    private sealed class SimpleFs : IFileSystem
    {
        public System.Collections.Generic.Dictionary<string, byte[]> Files { get; } = new();
        public Task<FileSystemResult> CopyFileAsync(string source, string dest, bool overwrite, CancellationToken ct) => Task.FromResult(FileSystemResult.Ok());
        public Task<FileSystemResult> MoveFileAsync(string source, string dest, bool overwrite, CancellationToken ct) => Task.FromResult(FileSystemResult.Ok());
        public Task<FileSystemResult> DeleteFileAsync(string path, CancellationToken ct) { Files.Remove(path); return Task.FromResult(FileSystemResult.Ok()); }
        public Task<FileSystemResult> CreateDirectoryAsync(string path, CancellationToken ct) => Task.FromResult(FileSystemResult.Ok());
        public Task<FileSystemResult> DeleteDirectoryAsync(string path, bool recursive, CancellationToken ct) => Task.FromResult(FileSystemResult.Ok());
        public Task<FileSystemResult<byte[]>> ReadAllBytesAsync(string path, CancellationToken ct)
            => Files.TryGetValue(path, out var b)
                ? Task.FromResult(FileSystemResult<byte[]>.Ok(b))
                : Task.FromResult(FileSystemResult<byte[]>.Fail(new FileSystemError(FileSystemErrorType.NotFound, "missing")));
        public Task<FileSystemResult> WriteAllBytesAsync(string path, byte[] data, CancellationToken ct) { Files[path] = data; return Task.FromResult(FileSystemResult.Ok()); }
        public Task<FileSystemResult<Stream>> OpenReadAsync(string path, CancellationToken ct) => Task.FromResult(FileSystemResult<Stream>.Ok(new MemoryStream()));
        public Task<FileSystemResult<Stream>> OpenWriteAsync(string path, CancellationToken ct) => Task.FromResult(FileSystemResult<Stream>.Ok(new MemoryStream()));
        public bool Exists(string path) => Files.ContainsKey(path);
        public bool DirectoryExists(string path) => false;
        public Task<string> ComputeSha256Async(string path, CancellationToken ct) => Task.FromResult("");
        public System.Collections.Generic.IEnumerable<string> EnumerateFiles(string path, string searchPattern = "*", bool recursive = false) => Array.Empty<string>();
        public long GetFileSize(string path) => Files.TryGetValue(path, out var b) ? b.Length : 0;
    }
}
