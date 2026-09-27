using Instella.CLI.Templates;
using NUnit.Framework;

namespace Instella.CLI.Tests;

[TestFixture]
public class ProjectTemplateTests
{
    [Test]
    public async Task ExtractAsync_ThePayloadReference_CarriesTheMetadataThatLetsItPublish()
    {
        // Without it a self-contained Release publish fails with NETSDK1150.
        var tempDir = Path.Combine(Path.GetTempPath(), $"instella-test-{Guid.NewGuid()}");
        try
        {
            await ProjectTemplate.ExtractAsync(tempDir, new TemplateOptions { AppName = "TestApp" });
            var csproj = await File.ReadAllTextAsync(Path.Combine(tempDir, "TestApp.Installer", "TestApp.Installer.csproj"));
            Assert.That(csproj, Does.Contain("<OutputItemType>_InstellaPayloadAssembly</OutputItemType>"));
            Assert.That(csproj, Does.Contain("<SkipGetTargetFrameworkProperties>true</SkipGetTargetFrameworkProperties>"));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Test]
    public async Task ExtractAsync_WritesAGitignoreThatKeepsKeysOut()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"instella-test-{Guid.NewGuid()}");
        try
        {
            await ProjectTemplate.ExtractAsync(tempDir, new TemplateOptions { AppName = "TestApp" });
            var gitignore = await File.ReadAllTextAsync(Path.Combine(tempDir, "TestApp.Installer", ".gitignore"));
            Assert.That(gitignore.Split('\n').Select(l => l.Trim()), Does.Contain("*.pem"));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Test]
    public async Task ExtractAsync_CreatesProjectDirectory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"instella-test-{Guid.NewGuid()}");
        try
        {
            var options = new TemplateOptions { AppName = "TestApp" };
            await ProjectTemplate.ExtractAsync(tempDir, options);
            Assert.That(Directory.Exists(Path.Combine(tempDir, "TestApp.Installer")), Is.True);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Test]
    public async Task ExtractAsync_CreatesCsprojFile()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"instella-test-{Guid.NewGuid()}");
        try
        {
            var options = new TemplateOptions
            {
                AppName = "TestApp",
                AppId = "com.test.app",
                ServerUrl = "https://test.example.com",
            };
            await ProjectTemplate.ExtractAsync(tempDir, options);

            var csprojPath = Path.Combine(tempDir, "TestApp.Installer", "TestApp.Installer.csproj");
            Assert.That(File.Exists(csprojPath), Is.True);

            var content = await File.ReadAllTextAsync(csprojPath);
            Assert.That(content, Does.Contain("<AssemblyName>TestApp.Installer</AssemblyName>"));
            Assert.That(content, Does.Contain("<OutputType>WinExe</OutputType>"));
            Assert.That(content, Does.Contain("Instella.Installer.Runtime"));
            Assert.That(content, Does.Contain("Instella.Installer.Build"));
            Assert.That(content, Does.Contain("<InstellaPayload>true</InstellaPayload>"));
            Assert.That(content, Does.Contain("<ReferenceOutputAssembly>false</ReferenceOutputAssembly>"));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Test]
    public async Task ExtractAsync_CreatesProgramCs_withFluentBuilderCall()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"instella-test-{Guid.NewGuid()}");
        try
        {
            var options = new TemplateOptions
            {
                AppName = "TestApp",
                AppId = "com.test.app",
                ServerUrl = "https://test.example.com",
            };
            await ProjectTemplate.ExtractAsync(tempDir, options);

            var programPath = Path.Combine(tempDir, "TestApp.Installer", "Program.cs");
            Assert.That(File.Exists(programPath), Is.True);

            var content = await File.ReadAllTextAsync(programPath);
            Assert.That(content, Does.Contain("using Instella.Installer.Runtime.Builders;"));
            Assert.That(content, Does.Contain("InstellaInstaller.Create()"));
            Assert.That(content, Does.Contain(".WithApp(\"TestApp\", \"com.test.app\""));
            Assert.That(content, Does.Contain(".WithServer(\"https://test.example.com\")"));
            Assert.That(content, Does.Contain(".Build()"));
            Assert.That(content, Does.Contain(".RunAsync(args)"));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Test]
    public async Task ExtractAsync_CreatesReadmeFile()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"instella-test-{Guid.NewGuid()}");
        try
        {
            var options = new TemplateOptions { AppName = "TestApp" };
            await ProjectTemplate.ExtractAsync(tempDir, options);

            var readmePath = Path.Combine(tempDir, "TestApp.Installer", "README.md");
            Assert.That(File.Exists(readmePath), Is.True);

            var content = await File.ReadAllTextAsync(readmePath);
            Assert.That(content, Does.Contain("# TestApp Installer"));
            Assert.That(content, Does.Contain("dotnet publish"));
            Assert.That(content, Does.Contain("dotnet run"));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Test]
    public async Task ExtractAsync_omitsInstellaJsonFile()
    {
        // The manifest is emitted at build time via --emit-manifest, so init
        // writes no static instella.json.
        var tempDir = Path.Combine(Path.GetTempPath(), $"instella-test-{Guid.NewGuid()}");
        try
        {
            var options = new TemplateOptions { AppName = "TestApp" };
            await ProjectTemplate.ExtractAsync(tempDir, options);

            var manifestPath = Path.Combine(tempDir, "TestApp.Installer", "instella.json");
            Assert.That(File.Exists(manifestPath), Is.False);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Test]
    public async Task ExtractAsync_GeneratesAppIdFromName_whenNotSupplied()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"instella-test-{Guid.NewGuid()}");
        try
        {
            var options = new TemplateOptions { AppName = "My Cool App" };
            await ProjectTemplate.ExtractAsync(tempDir, options);

            var programPath = Path.Combine(tempDir, "MyCoolApp.Installer", "Program.cs");
            var content = await File.ReadAllTextAsync(programPath);
            Assert.That(content, Does.Contain("com.example.my-cool-app"));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Test]
    public async Task ExtractAsync_SanitizesProjectNameWithSpaces()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"instella-test-{Guid.NewGuid()}");
        try
        {
            var options = new TemplateOptions { AppName = "My App Name" };
            await ProjectTemplate.ExtractAsync(tempDir, options);
            Assert.That(Directory.Exists(Path.Combine(tempDir, "MyAppName.Installer")), Is.True);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Test]
    public async Task ExtractAsync_SanitizesProjectNameWithSpecialChars()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"instella-test-{Guid.NewGuid()}");
        try
        {
            var options = new TemplateOptions { AppName = "My-App_Test123" };
            await ProjectTemplate.ExtractAsync(tempDir, options);
            Assert.That(Directory.Exists(Path.Combine(tempDir, "MyApp_Test123.Installer")), Is.True);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Test]
    public async Task ExtractAsync_UsesDefaultServerUrlPlaceholder_whenOmitted()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"instella-test-{Guid.NewGuid()}");
        try
        {
            var options = new TemplateOptions { AppName = "TestApp" };
            await ProjectTemplate.ExtractAsync(tempDir, options);

            var programPath = Path.Combine(tempDir, "TestApp.Installer", "Program.cs");
            var content = await File.ReadAllTextAsync(programPath);
            Assert.That(content, Does.Contain("https://updates.example.com"));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Test]
    public async Task ExtractAsync_EscapesXmlCharactersInCsproj()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"instella-test-{Guid.NewGuid()}");
        try
        {
            var options = new TemplateOptions { AppName = "Test<App>Name" };
            await ProjectTemplate.ExtractAsync(tempDir, options);

            var csprojPath = Path.Combine(tempDir, "TestAppName.Installer", "TestAppName.Installer.csproj");
            var content = await File.ReadAllTextAsync(csprojPath);
            // Name was sanitized before XML escaping — AssemblyName should not contain < or >.
            Assert.That(content, Does.Not.Contain("<AssemblyName>Test<"));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Test]
    public async Task ExtractAsync_CancellationTokenCancels()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"instella-test-{Guid.NewGuid()}");
        try
        {
            var options = new TemplateOptions { AppName = "TestApp" };
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var ex = Assert.CatchAsync<OperationCanceledException>(async () =>
                await ProjectTemplate.ExtractAsync(tempDir, options, cts.Token));
            Assert.That(ex, Is.Not.Null);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Test]
    public async Task ExtractAsync_HonorsAppProjectPathOverride()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"instella-test-{Guid.NewGuid()}");
        try
        {
            var options = new TemplateOptions
            {
                AppName = "TestApp",
                AppProjectPath = @"..\src\MyApp\MyApp.csproj",
            };
            await ProjectTemplate.ExtractAsync(tempDir, options);

            var csprojPath = Path.Combine(tempDir, "TestApp.Installer", "TestApp.Installer.csproj");
            var content = await File.ReadAllTextAsync(csprojPath);
            Assert.That(content, Does.Contain(@"..\src\MyApp\MyApp.csproj"));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
}
