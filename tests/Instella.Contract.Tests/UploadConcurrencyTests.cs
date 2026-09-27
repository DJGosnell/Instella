using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Instella.Core.Wire;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace Instella.Contract.Tests;

/// <summary>
/// Parallel upload sessions over an overlapping content set must leave every
/// stored file's reference count equal to the number of build files that reference it.
/// </summary>
[TestFixture]
public sealed class UploadConcurrencyTests
{
    private const string PackageId = "com.instella.concurrency";

    [Test]
    public async Task ParallelSessions_WithOverlappingContent_KeepReferenceCountsExact()
    {
        using var server = new ContractServer();
        var key = await server.SeedPackageAndKeyAsync(PackageId);
        var pool = Enumerable.Range(0, 8).Select(i => RandomNumberGenerator.GetBytes(512 + i)).ToArray();

        var sessions = Enumerable.Range(0, 20).Select(async i =>
        {
            using var client = server.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);

            using var start = await client.PostAsJsonAsync($"{ApiRoutes.Prefix}/{ApiRoutes.UploadStart}",
                new StartUploadRequest { PackageId = PackageId, Version = $"1.0.{i}", Os = "windows", Arch = "x64" });
            Assert.That(start.StatusCode, Is.EqualTo(HttpStatusCode.OK), await start.Content.ReadAsStringAsync());
            var session = (await start.Content.ReadFromJsonAsync<StartUploadResponse>())!.SessionId;

            // Five files per session drawn from the shared pool (sessions overlap heavily).
            for (var f = 0; f < 5; f++)
            {
                var content = pool[(i + f * 3) % pool.Length];
                var sha = Convert.ToHexStringLower(SHA256.HashData(content));
                using var file = await client.PostAsync($"{ApiRoutes.Prefix}/upload/{session}/file?path=f{f}.bin&sha256={sha}",
                    new ByteArrayContent(content));
                Assert.That(file.StatusCode, Is.EqualTo(HttpStatusCode.OK), await file.Content.ReadAsStringAsync());
            }

            using var complete = await client.PostAsJsonAsync($"{ApiRoutes.Prefix}/upload/{session}/complete", new { });
            Assert.That(complete.StatusCode, Is.EqualTo(HttpStatusCode.OK), await complete.Content.ReadAsStringAsync());
        });
        await Task.WhenAll(sessions);

        var counts = await server.QueryAsync(async db =>
        {
            var stored = await db.StoredFiles.Select(s => new { s.ContentHash, s.ReferenceCount }).ToListAsync();
            var referenced = await db.BuildFiles.GroupBy(b => b.ContentHash).Select(g => new { g.Key, Count = g.Count() }).ToListAsync();
            return stored.Select(s => (s.ContentHash, s.ReferenceCount, Actual: referenced.FirstOrDefault(r => r.Key == s.ContentHash)?.Count ?? 0)).ToList();
        });

        Assert.That(counts, Has.Count.EqualTo(pool.Length), "each content is stored once");
        Assert.That(counts.Where(c => c.ReferenceCount != c.Actual), Is.Empty,
            "ReferenceCount == COUNT(BuildFile) for every hash");
        Assert.That(counts.Sum(c => c.ReferenceCount), Is.EqualTo(20 * 5));
    }
}
