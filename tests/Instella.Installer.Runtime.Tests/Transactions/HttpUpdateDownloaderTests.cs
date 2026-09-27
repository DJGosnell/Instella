using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.Platform;
using Instella.Installer.Runtime.Core.Update;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.Transactions;

/// <summary>The updater's downloads are bounded and a bad patch archive is fetched once.</summary>
[TestFixture]
public class HttpUpdateDownloaderTests
{
    [Test]
    public async Task PatchArchiveHashMismatch_IsDownloadedOnce()
    {
        var handler = new CountingHandler(_ => Archive(("abc.patch", new byte[10])));
        using var downloader = Downloader(handler, expectedPatchSha256: new string('0', 64));

        Assert.ThrowsAsync<InvalidDataException>(() => downloader.OpenPatchEntryAsync("abc", 100, CancellationToken.None));
        var second = Assert.ThrowsAsync<InvalidDataException>(() => downloader.OpenPatchEntryAsync("abc", 100, CancellationToken.None));

        Assert.That(second!.Message, Does.Contain("patch archive unavailable"));
        Assert.That(handler.Requests, Is.EqualTo(1), "every later patched file falls back without downloading again");
    }

    [Test]
    public async Task OversizedPatchArchive_IsRefused()
    {
        var handler = new CountingHandler(_ => new byte[3 * 1024 * 1024]);
        using var downloader = Downloader(handler);
        downloader.SetReleaseSize(totalBytes: 1000, fileCount: 1);

        var ex = Assert.ThrowsAsync<InvalidDataException>(() => downloader.OpenPatchEntryAsync("abc", 100, CancellationToken.None));

        Assert.That(ex!.Message, Does.Contain("the patch archive is larger than"));
        await Task.CompletedTask;
    }

    [Test]
    public async Task PatchEntryLargerThanAllowed_IsRefused()
    {
        var handler = new CountingHandler(_ => Archive(("abc.patch", new byte[5000])));
        using var downloader = Downloader(handler);

        var ex = Assert.ThrowsAsync<InvalidDataException>(() => downloader.OpenPatchEntryAsync("abc", 4000, CancellationToken.None));

        Assert.That(ex!.Message, Does.Contain("more than the 4000 allowed"));
        await Task.CompletedTask;
    }

    [Test]
    public async Task OversizedFullBuild_IsRefused()
    {
        var handler = new CountingHandler(_ => new byte[200 * 1024]);
        using var downloader = Downloader(handler);
        downloader.SetReleaseSize(totalBytes: 1000, fileCount: 2);

        var ex = Assert.ThrowsAsync<InvalidDataException>(() => downloader.DownloadFullBuildAsync(CancellationToken.None));

        Assert.That(ex!.Message, Does.Contain("the full build is larger than"));
        await Task.CompletedTask;
    }

    private static HttpUpdateDownloader Downloader(HttpMessageHandler handler, string? expectedPatchSha256 = null) =>
        new(new HttpClient(handler), ownsHttp: true, "https://updates.example.com", "com.app", new Version(1, 0, 0),
            new Version(2, 0, 0), TargetPlatform.Windows, Architecture.X64, expectedPatchSha256);

    private static byte[] Archive(params (string Name, byte[] Bytes)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, bytes) in entries)
            {
                using var s = zip.CreateEntry(name, CompressionLevel.NoCompression).Open();
                s.Write(bytes);
            }
        }
        return ms.ToArray();
    }

    private sealed class CountingHandler(Func<HttpRequestMessage, byte[]> body) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body(request)) });
        }
    }
}
