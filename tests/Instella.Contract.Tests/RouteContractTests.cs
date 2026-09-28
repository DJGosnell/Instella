using Instella.Core.Platform;
using Instella.Core.Wire;
using NUnit.Framework;

namespace Instella.Contract.Tests;

/// <summary>
/// One test per <see cref="ApiRoutes"/> builder: the URL a client builds must be matched
/// by the server's routing, to the intended action, with the intended parameter values.
/// This catches template drift without running full flows.
/// </summary>
[TestFixture]
public class RouteContractTests
{
    private static readonly Uri Server = new("https://updates.example.com/base/");
    private ContractServer _server = null!;

    [OneTimeSetUp]
    public void SetUp()
    {
        _server = new ContractServer();
        _ = _server.Server; // start the host so the endpoint data source is populated
    }

    [OneTimeTearDown]
    public void TearDown() => _server.Dispose();

    private static readonly Version V1 = new(1, 2, 0);
    private static readonly Version V2 = new(1, 3, 0);
    private static readonly Guid Session = Guid.Parse("7f3c2b1a-0000-4000-8000-000000000001");

    private static IEnumerable<TestCaseData> Cases()
    {
        yield return Case("GET", ApiRoutes.ForCheckUpdate(Server, "com.x.app", V1, TargetPlatform.Windows, Architecture.ARM32, "beta"),
            "CheckUpdate");
        yield return Case("GET", ApiRoutes.ForRelease(Server, "com.x.app", V2, TargetPlatform.Windows, Architecture.X64),
            "GetRelease", ("packageId", "com.x.app"), ("version", "1.3.0"), ("os", "windows"), ("arch", "x64"));
        yield return Case("GET", ApiRoutes.ForDownloadBuild(Server, "com.x.app", V2, TargetPlatform.Windows, Architecture.X64),
            "DownloadBuild", ("packageId", "com.x.app"), ("version", "1.3.0"), ("os", "windows"), ("arch", "x64"));
        yield return Case("GET", ApiRoutes.ForDownloadFile(Server, "com.x.app", V2, TargetPlatform.Linux, Architecture.ARM64, "sub dir/My App.dll"),
            "DownloadFile", ("os", "linux"), ("arch", "arm64"), ("path", "sub dir/My App.dll"));
        yield return Case("GET", ApiRoutes.ForPatch(Server, "com.x.app", V1, V2, TargetPlatform.MacOS, Architecture.X86),
            "DownloadPatch", ("fromVersion", "1.2.0"), ("toVersion", "1.3.0"), ("os", "macos"), ("arch", "x86"));
        yield return Case("GET", ApiRoutes.ForPatchManifest(Server, "com.x.app", V1, V2, TargetPlatform.Windows, Architecture.X64),
            "GetPatchManifest", ("fromVersion", "1.2.0"), ("toVersion", "1.3.0"));
        yield return Case("GET", ApiRoutes.ForPackages(Server), "GetPackages");
        yield return Case("GET", ApiRoutes.ForPackage(Server, "com.x.app"), "GetPackage", ("packageId", "com.x.app"));
        yield return Case("GET", ApiRoutes.ForPackageVersions(Server, "com.x.app"), "GetVersions", ("packageId", "com.x.app"));
        yield return Case("DELETE", ApiRoutes.ForPackageVersion(Server, "com.x.app", "1.3.0"), "DeleteVersion", ("version", "1.3.0"));
        yield return Case("PUT", ApiRoutes.ForPackageVersion(Server, "com.x.app", "1.3.0"), "UpdateVersion", ("version", "1.3.0"));
        yield return Case("POST", ApiRoutes.ForUploadStart(Server), "StartUpload");
        yield return Case("POST", ApiRoutes.ForUploadFile(Server, Session, "a/b.dll", new string('0', 64)), "UploadFile",
            ("sessionId", Session.ToString("D")));
        yield return Case("POST", ApiRoutes.ForUploadComplete(Server, Session), "CompleteUpload", ("sessionId", Session.ToString("D")));
        yield return Case("DELETE", ApiRoutes.ForUploadCancel(Server, Session), "CancelUpload", ("sessionId", Session.ToString("D")));
        yield return Case("POST", ApiRoutes.ForUploadInstaller(Server, Session, InstallerKinds.Online, "My Setup (x64).exe", new string('0', 64)),
            "UploadInstaller", ("sessionId", Session.ToString("D")));
        yield return Case("GET", ApiRoutes.ForDownloadInstaller(Server, "com.x.app", V2, TargetPlatform.Windows, Architecture.ARM64, InstallerKinds.Offline),
            "DownloadInstaller", ("version", "1.3.0"), ("os", "windows"), ("arch", "arm64"), ("kind", "offline"));
        yield return Case("GET", ApiRoutes.ForLatestInstaller(Server, "com.x.app", TargetPlatform.Linux, Architecture.X64, InstallerKinds.Online, "beta"),
            "DownloadInstaller", ("version", ApiRoutes.LatestVersion), ("os", "linux"), ("kind", "online"));
        yield return Case("GET", ApiRoutes.ForDraft(Server, "com.x.app", V2, TargetPlatform.Windows, Architecture.X64),
            "GetDraft", ("version", "1.3.0"), ("os", "windows"), ("arch", "x64"));
        yield return Case("POST", ApiRoutes.ForPublishDraft(Server, "com.x.app", V2, TargetPlatform.MacOS, Architecture.ARM64),
            "PublishDraft", ("version", "1.3.0"), ("os", "macos"), ("arch", "arm64"));
        yield return Case("GET", ApiRoutes.ForApprovals(Server, "com.x.app"), nameof(Instella.Server.Api.ApprovalsController.List), ("packageId", "com.x.app"));
        yield return Case("GET", ApiRoutes.ForApproval(Server, "com.x.app", V2, TargetPlatform.Windows, Architecture.X64),
            nameof(Instella.Server.Api.ApprovalsController.Get), ("version", "1.3.0"), ("os", "windows"), ("arch", "x64"));
        yield return Case("POST", ApiRoutes.ForApproveRelease(Server, "com.x.app", V2, TargetPlatform.Linux, Architecture.ARM64),
            nameof(Instella.Server.Api.ApprovalsController.Approve), ("version", "1.3.0"), ("os", "linux"), ("arch", "arm64"));
        yield return Case("POST", ApiRoutes.ForRejectRelease(Server, "com.x.app", V2, TargetPlatform.Windows, Architecture.X86),
            nameof(Instella.Server.Api.ApprovalsController.Reject), ("version", "1.3.0"), ("os", "windows"), ("arch", "x86"));
    }

    private static TestCaseData Case(string method, Uri uri, string action, params (string Key, string Value)[] values) =>
        new TestCaseData(method, uri, action, values).SetName($"{method} {action}");

    [TestCaseSource(nameof(Cases))]
    public void ClientUrl_IsMatchedByServerRoute(string method, Uri uri, string action, (string Key, string Value)[] values)
    {
        // Strip the client's base path: the server is hosted at the root, and the
        // builder must have kept the base path intact ahead of the prefix.
        Assert.That(uri.AbsolutePath, Does.StartWith("/base/" + ApiRoutes.Prefix + "/"));
        var local = new Uri("http://localhost" + uri.AbsolutePath["/base".Length..] + uri.Query);

        var match = _server.MatchRoute(method, local);

        Assert.That(match, Is.Not.Null, $"no server route matches {method} {local}");
        Assert.That(match!.Value.Action, Is.EqualTo(action));
        foreach (var (key, value) in values)
            Assert.That(match.Value.Values[key]?.ToString(), Is.EqualTo(value), key);
    }

    [Test]
    public void CheckUpdate_QueryCarriesCanonicalPlatformNames()
    {
        var uri = ApiRoutes.ForCheckUpdate(Server, "com.x.app", V1, TargetPlatform.MacOS, Architecture.ARM32, "stable");
        Assert.That(uri.Query, Does.Contain("os=macos").And.Contain("arch=arm32").And.Contain("currentVersion=1.2.0"));
    }

    [TestCase("arm", Architecture.ARM32)]
    [TestCase("ARM32", Architecture.ARM32)]
    [TestCase("aarch64", Architecture.ARM64)]
    [TestCase("amd64", Architecture.X64)]
    [TestCase("x86", Architecture.X86)]
    public void PlatformStrings_AcceptAliases(string name, Architecture expected)
    {
        Assert.That(PlatformStrings.TryParseArch(name, out var arch), Is.True);
        Assert.That(arch, Is.EqualTo(expected));
    }

    [Test]
    public void PlatformStrings_RoundTripEveryValue()
    {
        foreach (var a in Enum.GetValues<Architecture>())
        {
            Assert.That(PlatformStrings.TryParseArch(PlatformStrings.Arch(a), out var back), Is.True);
            Assert.That(back, Is.EqualTo(a));
        }
        foreach (var p in Enum.GetValues<TargetPlatform>())
        {
            Assert.That(PlatformStrings.TryParseOs(PlatformStrings.Os(p), out var back), Is.True);
            Assert.That(back, Is.EqualTo(p));
        }
    }
}
