using System.Net;
using Instella.Core.Wire;
using Instella.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace Instella.Contract.Tests;

/// <summary>
/// One address hammering the package API is rate limited, and its denials do not fill
/// the security log. Its own server, so the spent rate-limit window affects no other test.
/// </summary>
[TestFixture]
public sealed class FloodContractTests
{
    [Test]
    public async Task SevenHundredAnonymousReads_AreLimited_AndLoggedAtMostTwice()
    {
        using var server = new ContractServer();
        await server.QueryAsync(async db =>
        {
            db.Packages.Add(new Package
            {
                PackageId = "com.instella.flood", DisplayName = "flood",
                DownloadAccessMode = DownloadAccessMode.PackageKeyRequired, CreatedAt = DateTime.UtcNow,
            });
            return await db.SaveChangesAsync();
        });
        using var http = server.CreateClient();

        var limited = 0;
        for (var i = 0; i < 700; i++)
        {
            using var response = await http.GetAsync($"{ApiRoutes.Prefix}/packages/com.instella.flood");
            if (response.StatusCode == HttpStatusCode.TooManyRequests) limited++;
            else Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        }

        Assert.That(limited, Is.GreaterThanOrEqualTo(400), "300 per minute per address by default");
        var events = await server.QueryAsync(db => db.SecurityEvents.CountAsync(e => e.PackageId == "com.instella.flood"));
        Assert.That(events, Is.InRange(1, 2), "the first denial, plus at most one \"(+N similar)\" summary");
    }
}
