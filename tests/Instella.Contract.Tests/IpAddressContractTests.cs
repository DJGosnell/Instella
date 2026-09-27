using System.Net;
using Instella.Core.Wire;
using Instella.Server.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Instella.Contract.Tests;

/// <summary>An IPv4 client seen as IPv4-mapped IPv6 is the same client for bans.</summary>
[TestFixture]
public sealed class IpAddressContractTests
{
    [Test]
    public async Task ABanOnTheIpv4Address_CoversItsIpv6MappedForm()
    {
        using var server = new ContractServer(IPAddress.Parse("::ffff:198.51.100.7"));
        await BanAsync(server, "198.51.100.7");

        using var response = await server.CreateClient().GetAsync($"{ApiRoutes.Prefix}/{ApiRoutes.Packages}");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [Test]
    public async Task ABanEnteredInTheMappedForm_IsStoredAsIpv4()
    {
        using var server = new ContractServer();

        await BanAsync(server, "::ffff:198.51.100.7");

        Assert.That(await server.QueryAsync(db => db.IpBans.Select(b => b.IpAddress).SingleAsync()), Is.EqualTo("198.51.100.7"));
        using var response = await server.CreateClient().GetAsync($"{ApiRoutes.Prefix}/{ApiRoutes.Packages}");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden), "and it bans the IPv4 client");
    }

    [Test]
    public void ABanOnSomethingThatIsNotAnAddress_IsRefused()
    {
        using var server = new ContractServer();

        var ex = Assert.ThrowsAsync<ArgumentException>(() => BanAsync(server, "abc"));

        Assert.That(ex!.Message, Does.StartWith("'abc' is not an IP address"));
    }

    private static async Task BanAsync(ContractServer server, string address)
    {
        using var scope = server.Services.CreateScope();
        var admin = await scope.ServiceProvider.GetRequiredService<AuthService>().CreateAdminUserAsync("admin", "correct horse battery");
        await scope.ServiceProvider.GetRequiredService<IIpBanService>().AddBanAsync(address, "test", admin.Id);
    }
}
