using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Instella.CLI.Commands;
using Instella.CLI.Services;
using Instella.Core.Platform;
using Instella.Core.Trust;
using Instella.Core.Wire;
using NUnit.Framework;

namespace Instella.CLI.Tests;

/// <summary>
/// <c>instella approve</c>, <c>reject</c> and <c>pending</c> against a scripted server: approve sends the
/// hash of exactly the manifest it showed, never acts without confirmation, and maps the server's answers
/// to exit codes; upload and publish report a pending release as a success.
/// </summary>
[TestFixture]
public class ReviewCommandTests
{
    private static readonly ReleaseTarget Target = new("com.app", new Version(1, 2, 0), TargetPlatform.Windows, Architecture.X64);

    [Test]
    public async Task Approve_ShowsTheRelease_AndApprovesTheManifestItShowed()
    {
        var bytes = ManifestBytes();
        var server = new ScriptedServer().Release(bytes, ReleaseStates.Pending).Answer("approve", HttpStatusCode.OK, """{"message":"Approved 1.2.0 windows/x64"}""");
        var console = Console(input: "y\n");

        var exit = await ApproveCommand.RunAsync(server.Client, Target, null, null, null, yes: false, console, CancellationToken.None);

        Assert.That(exit, Is.EqualTo(ExitCodes.Success), console.Error.ToString());
        var output = console.Out.ToString()!;
        Assert.That(output, Does.Contain("Pending com.app 1.2.0 windows/x64").And.Contain("Signed by: abcd1234abcd1234 (ci)")
            .And.Contain("Uploaded by API key: ci-upload").And.Contain("Approved 1.2.0"));
        var body = JsonDocument.Parse(server.Bodies["approve"]).RootElement;
        Assert.That(body.GetProperty("manifestSha256").GetString(), Is.EqualTo(Convert.ToHexStringLower(SHA256.HashData(bytes))));
    }

    [Test]
    public async Task Approve_OnRedirectedInput_WithoutYes_DoesNothing()
    {
        var server = new ScriptedServer().Release(ManifestBytes(), ReleaseStates.Pending);
        var console = Console(input: "", redirected: true);

        var exit = await ApproveCommand.RunAsync(server.Client, Target, null, null, null, yes: false, console, CancellationToken.None);

        Assert.That(exit, Is.EqualTo(ExitCodes.Usage));
        Assert.That(console.Error.ToString(), Does.Contain("Pass --yes"));
        Assert.That(server.Bodies.ContainsKey("approve"), Is.False);
    }

    [Test]
    public async Task Approve_ADraft_IsRefused()
    {
        var server = new ScriptedServer().Release(ManifestBytes(), ReleaseStates.Draft);
        var console = Console();

        var exit = await ApproveCommand.RunAsync(server.Client, Target, null, null, null, yes: true, console, CancellationToken.None);

        Assert.That(exit, Is.EqualTo(ExitCodes.Usage));
        Assert.That(console.Error.ToString(), Does.Contain("instella publish"));
    }

    [Test]
    public async Task Approve_FilesThatDoNotMatchPath_IsRefused()
    {
        var dir = Directory.CreateTempSubdirectory("instella-approve-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "App.exe"), "something else");
            var server = new ScriptedServer().Release(ManifestBytes(), ReleaseStates.Pending);
            var console = Console();

            var exit = await ApproveCommand.RunAsync(server.Client, Target, new DirectoryInfo(dir), null, null, yes: true, console, CancellationToken.None);

            Assert.That(exit, Is.EqualTo(ExitCodes.Usage));
            Assert.That(console.Error.ToString(), Does.Contain("Refusing to approve"));
            Assert.That(server.Bodies.ContainsKey("approve"), Is.False);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [TestCase(HttpStatusCode.Forbidden, ExitCodes.Auth)]
    [TestCase(HttpStatusCode.Conflict, ExitCodes.Server)]
    public async Task Approve_ServerRefusal_MapsToTheExitCode(HttpStatusCode status, int expected)
    {
        var server = new ScriptedServer().Release(ManifestBytes(), ReleaseStates.Pending)
            .Answer("approve", status, """{"error":"An API key cannot approve its own upload"}""");
        var console = Console();

        var exit = await ApproveCommand.RunAsync(server.Client, Target, null, null, null, yes: true, console, CancellationToken.None);

        Assert.That(exit, Is.EqualTo(expected));
        Assert.That(console.Error.ToString(), Does.Contain("Approve failed: An API key cannot approve its own upload"));
    }

    [Test]
    public async Task Reject_SendsTheReason()
    {
        var server = new ScriptedServer().Release(ManifestBytes(), ReleaseStates.Draft).Answer("reject", HttpStatusCode.OK, """{"message":"Rejected"}""");
        var console = Console();

        var exit = await RejectCommand.RunAsync(server.Client, Target, "wrong tag", yes: true, console, CancellationToken.None);

        Assert.That(exit, Is.EqualTo(ExitCodes.Success), console.Error.ToString());
        Assert.That(console.Out.ToString(), Does.Contain("Draft com.app 1.2.0"));
        Assert.That(JsonDocument.Parse(server.Bodies["reject"]).RootElement.GetProperty("reason").GetString(), Is.EqualTo("wrong tag"));
    }

    [Test]
    public async Task Pending_ListsEachRelease()
    {
        var server = new ScriptedServer().Answer("approvals/com.app", HttpStatusCode.OK, """
            [{"version":"1.2.0","os":"windows","arch":"x64","state":"pending","uploadedAt":"2026-09-28T10:00:00Z","publishAfter":"2026-09-29T10:00:00Z","uploadedBy":"ci","keyId":"k","keyLabel":"ci key"},
             {"version":"1.3.0","os":"windows","arch":"x64","state":"draft","uploadedAt":"2026-09-28T11:00:00Z","uploadedBy":"ci"}]
            """);
        var console = Console();

        var exit = await PendingCommand.RunAsync(server.Client, "com.app", console, CancellationToken.None);

        Assert.That(exit, Is.EqualTo(ExitCodes.Success));
        Assert.That(console.Out.ToString(), Does.Contain("pending, publishes at 2026-09-29 10:00:00Z").And.Contain("signed by ci key")
            .And.Contain("draft (unsigned)"));
    }

    [TestCase(ReleaseStates.Published, null, "published")]
    [TestCase(ReleaseStates.Pending, null, "Pending approval")]
    [TestCase(ReleaseStates.Pending, "2026-09-29T10:00:00Z", "publishes it automatically at 2026-09-29 10:00:00Z")]
    [TestCase(ReleaseStates.Draft, null, "Draft uploaded")]
    [TestCase(null, null, "Upload successful.")]
    public void Upload_ReportsTheServersState(string? state, string? publishAfter, string expected)
    {
        var result = new UploadResult(true, null, null)
        {
            State = state,
            PublishAfter = publishAfter is null ? null : DateTime.Parse(publishAfter, null, System.Globalization.DateTimeStyles.AdjustToUniversal),
        };

        Assert.That(UploadCommand.SuccessMessage(result, draft: false, "instella publish ..."), Does.Contain(expected));
    }

    private static byte[] ManifestBytes() => JsonSerializer.SerializeToUtf8Bytes(new ReleaseManifest
    {
        FormatVersion = ReleaseManifest.CurrentFormatVersion,
        AppId = "com.app",
        Version = new Version(1, 2, 0),
        Os = "windows",
        Arch = "x64",
        Channel = "stable",
        CreatedAt = DateTimeOffset.UtcNow,
        Files = [new ReleaseFile("App.exe", 3, Convert.ToHexStringLower(SHA256.HashData("app"u8)))],
    }, TrustJsonContext.Default.ReleaseManifest);

    private static CommandConsole Console(string input = "", bool redirected = false) =>
        new(new StringWriter(), new StringWriter(), new StringReader(input), redirected);

    /// <summary>Answers by the last path segment(s) it knows; records request bodies by that name.</summary>
    private sealed class ScriptedServer : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _answers = new();

        public Dictionary<string, string> Bodies { get; } = new();

        public ApiClient Client => new(new HttpClient(this), "https://updates.example.com/", "key");

        public ScriptedServer Release(byte[] manifest, string state)
        {
            var summary = new UnpublishedReleaseSummary
            {
                Version = "1.2.0", Os = "windows", Arch = "x64", State = state, UploadedAt = DateTime.UtcNow,
                KeyId = state == ReleaseStates.Draft ? null : "abcd1234abcd1234", KeyLabel = "ci", UploadedBy = "ci-upload",
                ManifestSha256 = Convert.ToHexStringLower(SHA256.HashData(manifest)),
            };
            var body = JsonSerializer.Serialize(new UnpublishedReleaseResponse { Summary = summary, Manifest = Convert.ToBase64String(manifest) },
                WireJsonContext.Default.UnpublishedReleaseResponse);
            return Answer("x64", HttpStatusCode.OK, body);
        }

        public ScriptedServer Answer(string key, HttpStatusCode status, string body)
        {
            _answers[key] = (status, body);
            return this;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            var key = _answers.Keys.OrderByDescending(k => k.Length).FirstOrDefault(k => path.EndsWith("/" + k, StringComparison.Ordinal))
                ?? throw new InvalidOperationException($"unexpected request {path}");
            if (request.Content is not null)
                Bodies[key] = await request.Content.ReadAsStringAsync(ct);
            var (status, body) = _answers[key];
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
