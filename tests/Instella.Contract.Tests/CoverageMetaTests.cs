using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using NUnit.Framework;

namespace Instella.Contract.Tests;

/// <summary>
/// No percentage gate, but each high-risk seam listed here must be referenced by some test. Reads the type references out of every built test assembly.
/// </summary>
[TestFixture]
public sealed class CoverageMetaTests
{
    private static readonly string[] TestAssemblies =
    [
        "Instella.Core.Tests", "Instella.Installer.Runtime.Tests", "Instella.Server.Tests",
        "Instella.Contract.Tests", "Instella.Sdk.Tests", "Instella.Installer.Testing.Tests",
    ];

    [Test]
    public void EveryCriticalSeam_IsReferencedByATest()
    {
        var referenced = ReferencedTypes();

        var required = new List<string>
        {
            "Instella.Installer.Runtime.Core.Transactions.*",
            "Instella.Installer.Runtime.Core.Update.*",
            "Instella.Core.Trust.*",
            "Instella.Core.FileSystem.SafePath",
            "Instella.Server.Services.PackageService",
            "Instella.Server.Services.UploadService",
            "Instella.Server.Services.ReleaseApprovalService",
            "Instella.Server.Api.ApprovalsController",
            "Instella.Server.Services.DelayedReleaseWorker",
        };
        // Each runner, individually.
        required.AddRange(typeof(Instella.Installer.Runtime.Builders.InstellaInstaller).Assembly.GetTypes()
            .Where(t => t.Namespace == "Instella.Installer.Runtime.Runners" && t.Name.EndsWith("Runner", StringComparison.Ordinal))
            .Select(t => t.FullName!));

        var missing = required.Where(r => r.EndsWith(".*", StringComparison.Ordinal)
                ? !referenced.Any(t => t.StartsWith(r[..^1], StringComparison.Ordinal))
                : !referenced.Contains(r))
            .ToList();

        Assert.That(missing, Is.Empty, "types or namespaces no test references");
    }

    /// <summary>Full names of every type the test assemblies reference or define.</summary>
    private static HashSet<string> ReferencedTypes()
    {
        var here = Path.GetDirectoryName(typeof(CoverageMetaTests).Assembly.Location)!;
        var tfm = new DirectoryInfo(here).Name;
        var configuration = new DirectoryInfo(here).Parent!.Name;
        var repo = new DirectoryInfo(here);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "Instella.sln")))
            repo = repo.Parent;
        Assert.That(repo, Is.Not.Null, "repository root not found");

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in TestAssemblies)
        {
            var path = Path.Combine(repo!.FullName, "tests", name, "bin", configuration, tfm, name + ".dll");
            if (!File.Exists(path))
                Assert.Ignore($"{name} is not built ({path}); build the solution to run this check");

            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            var md = pe.GetMetadataReader();
            foreach (var handle in md.TypeReferences)
            {
                var type = md.GetTypeReference(handle);
                names.Add($"{md.GetString(type.Namespace)}.{md.GetString(type.Name)}");
            }
        }
        return names;
    }
}
