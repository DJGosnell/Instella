using Bunit;
using Instella.Server.Components;
using Instella.Server.Services;
using Instella.Server.Tests.Infrastructure;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Instella.Server.Tests.Integration;

/// <summary>
/// Every admin page, rendered through the app's own router for an anonymous
/// user, shows only the NotAuthorized content — a redirect to the login page — and never the
/// page itself.
/// </summary>
[TestFixture]
public sealed class AdminPageAuthorizationTests
{
    [TestCase("/")]
    [TestCase("/packages")]
    [TestCase("/api-keys")]
    [TestCase("/settings")]
    [TestCase("/docs")]
    public async Task AdminPage_Anonymous_RedirectsToLogin(string page)
    {
        using var db = new DatabaseFixture();
        await new AuthService(db.Context).CreateAdminUserAsync("admin", "correct horse battery");

        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton(db.Context);
        ctx.Services.AddScoped(_ => new AuthService(db.Context));
        ctx.AddAuthorization();   // anonymous

        var navigation = ctx.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(page);

        var cut = ctx.Render<Routes>();

        cut.WaitForAssertion(() =>
            Assert.That(navigation.Uri, Does.Contain("/login?returnUrl=")));
        Assert.That(cut.FindAll("nav.sidebar"), Is.Empty, "no admin chrome is rendered for an anonymous user");
        Assert.That(cut.FindAll("form[action='/api/auth/login']"), Has.Count.EqualTo(1), "the router landed on the login page");
    }

    [Test]
    public void AdminPage_BeforeSetup_RedirectsToSetup()
    {
        using var db = new DatabaseFixture();
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddScoped(_ => new AuthService(db.Context));
        // The router goes on to render the setup page itself.
        var configDir = Directory.CreateTempSubdirectory("instella-setup-").FullName;
        ctx.Services.AddSingleton(new SetupTokenService(configDir, Microsoft.Extensions.Logging.Abstractions.NullLogger<SetupTokenService>.Instance));
        ctx.Services.AddScoped(_ => new StorageSettingsService(db.Context, SecretProtector.None));
        ctx.AddAuthorization();

        var navigation = ctx.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/api-keys");

        var cut = ctx.Render<Routes>();

        cut.WaitForAssertion(() => Assert.That(navigation.Uri, Does.EndWith("/setup")));
    }
}
