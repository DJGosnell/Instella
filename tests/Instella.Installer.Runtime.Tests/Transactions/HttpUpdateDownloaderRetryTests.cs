using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.Platform;
using Instella.Installer.Runtime.Core.Update;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.Transactions;

/// <summary>8.9: installs behind one NAT address share the server's rate limit, so a 429 is retried.</summary>
[TestFixture]
public sealed class HttpUpdateDownloaderRetryTests
{
    [Test]
    public async Task A429_IsRetried_AfterRetryAfter_AndSucceeds()
    {
        var handler = new Sequence(
            () => Limited(TimeSpan.FromSeconds(2)),
            () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("content") });
        var waits = new List<TimeSpan>();
        using var downloader = Downloader(handler, waits);

        await using var stream = await downloader.DownloadFileAsync("App.exe", CancellationToken.None);

        Assert.That(new System.IO.StreamReader(stream).ReadToEnd(), Is.EqualTo("content"));
        Assert.That(waits, Is.EqualTo(new[] { TimeSpan.FromSeconds(2) }));
        Assert.That(handler.Calls, Is.EqualTo(2));
    }

    [Test]
    public void A429_IsRetriedThreeTimes_ThenReported_AndLongWaitsAreCapped()
    {
        var handler = new Sequence(() => Limited(TimeSpan.FromMinutes(10)));
        var waits = new List<TimeSpan>();
        using var downloader = Downloader(handler, waits);

        Assert.ThrowsAsync<HttpRequestException>(() => downloader.DownloadFileAsync("App.exe", CancellationToken.None));

        Assert.That(handler.Calls, Is.EqualTo(4), "the first try and three retries");
        Assert.That(waits, Is.All.EqualTo(TimeSpan.FromSeconds(30)));
    }

    private static HttpUpdateDownloader Downloader(HttpMessageHandler handler, List<TimeSpan> waits) =>
        new(new HttpClient(handler), ownsHttp: true, "https://updates.example.com", "com.app", new Version(1, 0, 0), new Version(1, 1, 0),
            TargetPlatform.Windows, Architecture.X64)
        {
            Delay = (wait, _) => { waits.Add(wait); return Task.CompletedTask; },
        };

    private static HttpResponseMessage Limited(TimeSpan retryAfter)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(retryAfter);
        return response;
    }

    /// <summary>Answers with the responses in order; the last one repeats.</summary>
    private sealed class Sequence(params Func<HttpResponseMessage>[] responses) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(responses[Math.Min(Calls++, responses.Length - 1)]());
    }
}
