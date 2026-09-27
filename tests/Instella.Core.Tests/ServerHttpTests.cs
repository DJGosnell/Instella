using System.Net;
using Instella.Core.Wire;
using NUnit.Framework;

namespace Instella.Core.Tests;

/// <summary>The download token goes to the update server itself and nowhere else.</summary>
[TestFixture]
public sealed class ServerHttpTests
{
    private const string Token = "idt_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    [TestCase("https://updates.example.com/api/v1/check-update?packageId=x", true)]
    [TestCase("https://UPDATES.example.com/api/v1/packages", true)]
    [TestCase("https://s3.example.com/bucket/ab/cd/blob", false)]
    [TestCase("https://updates.example.com:8443/api/v1/packages", false)]
    [TestCase("http://updates.example.com/api/v1/packages", false)]
    public async Task TheToken_IsSentOnlyToTheServer(string url, bool sent)
    {
        var capture = new Capture();
        using var http = ServerHttp.Create(new Uri("https://updates.example.com/"), Token, "Instella-Test/1.0",
            TimeSpan.FromSeconds(5), capture);

        using var _ = await http.GetAsync(url);

        Assert.That(capture.Authorization, sent ? Is.EqualTo("Bearer " + Token) : Is.Null);
        Assert.That(capture.UserAgent, Is.EqualTo("Instella-Test/1.0"));
    }

    [Test]
    public async Task WithoutAToken_NothingIsSent()
    {
        var capture = new Capture();
        using var http = ServerHttp.Create(new Uri("https://updates.example.com/"), null, "Instella-Test/1.0",
            TimeSpan.FromSeconds(5), capture);

        using var _ = await http.GetAsync("https://updates.example.com/api/v1/packages");

        Assert.That(capture.Authorization, Is.Null);
    }

    private sealed class Capture : HttpMessageHandler
    {
        public string? Authorization { get; private set; }
        public string? UserAgent { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Authorization = request.Headers.Authorization?.ToString();
            UserAgent = request.Headers.UserAgent.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
