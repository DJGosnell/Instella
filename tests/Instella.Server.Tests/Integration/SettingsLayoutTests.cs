using Bunit;
using Instella.Server.Components.Pages;
using Instella.Server.Data;
using Instella.Server.Services;
using Instella.Server.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Instella.Server.Tests.Integration;

/// <summary>
/// Hand test: the Security card's event table ran out of its box. The card sat inside the
/// masonry grid, whose cards are inline-block, so its <c>column-span: all</c> never applied and
/// the card was one column wide. It is now a full-width row after the grid.
/// </summary>
[TestFixture]
public sealed class SettingsLayoutTests
{
    [Test]
    public async Task SecurityCard_IsAFullWidthRow_OutsideTheMasonryGrid()
    {
        using var db = new DatabaseFixture();
        await new AuthService(db.Context).CreateAdminUserAsync("admin", "correct horse battery");

        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton<AppDbContext>(db.Context);
        ctx.Services.AddScoped(_ => new AuthService(db.Context));
        ctx.Services.AddScoped(_ => new StorageSettingsService(db.Context, SecretProtector.None));
        ctx.Services.AddScoped<ISecurityLogService>(_ => new SecurityLogService(db.Context));
        ctx.Services.AddSingleton<IpBanCache>();
        ctx.Services.AddScoped<IIpBanService, IpBanService>();
        ctx.Services.AddSingleton<IRateLimitService, RateLimitService>();
        ctx.AddAuthorization().SetAuthorized("admin");

        var cut = ctx.Render<Settings>();

        var security = cut.WaitForElement(".wide-card");
        Assert.That(security.QuerySelector("h2")?.TextContent, Is.EqualTo("Security"));
        Assert.That(security.Closest(".settings-grid"), Is.Null);
        Assert.That(cut.FindAll(".settings-grid .card"), Is.Not.Empty, "the other cards stay in the grid");
    }
}
