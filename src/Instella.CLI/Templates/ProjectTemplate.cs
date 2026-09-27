using System.Text;

namespace Instella.CLI.Templates;

/// <summary>
/// Scaffolds an Instella installer project alongside the user's existing
/// app. Emits a <c>.csproj</c> that references the user's app via
/// <c>&lt;ProjectReference InstellaPayload="true"&gt;</c>, a fluent-builder
/// <c>Program.cs</c>, and a README explaining the build flow. No
/// <c>instella.json</c> is generated — the manifest is produced at build
/// time by the installer's <c>--emit-manifest</c> flag.
/// </summary>
public static class ProjectTemplate
{
    /// <summary>
    /// Extracts the installer project into <paramref name="outputDir"/>. The
    /// resulting directory is <c>{outputDir}/{SanitizedAppName}.Installer/</c>
    /// with <c>{SanitizedAppName}.Installer.csproj</c>, <c>Program.cs</c>,
    /// and <c>README.md</c>.
    /// </summary>
    public static async Task ExtractAsync(
        string outputDir,
        TemplateOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(outputDir);
        ArgumentNullException.ThrowIfNull(options);

        if (!Directory.Exists(outputDir))
            Directory.CreateDirectory(outputDir);

        var projectName = SanitizeProjectName(options.AppName);
        var projectDir = Path.Combine(outputDir, $"{projectName}.Installer");
        Directory.CreateDirectory(projectDir);

        var csproj = GenerateCsproj(options, projectName);
        var program = GenerateProgramCs(options, projectName);
        var readme = GenerateReadme(options, projectName);

        await File.WriteAllTextAsync(
            Path.Combine(projectDir, $"{projectName}.Installer.csproj"),
            csproj,
            Encoding.UTF8,
            ct);

        await File.WriteAllTextAsync(
            Path.Combine(projectDir, "Program.cs"),
            program,
            Encoding.UTF8,
            ct);

        await File.WriteAllTextAsync(
            Path.Combine(projectDir, "README.md"),
            readme,
            Encoding.UTF8,
            ct);

        // A signing key must never be committed next to the installer.
        await File.WriteAllTextAsync(Path.Combine(projectDir, ".gitignore"), "# Private signing keys\n*.pem\n", Encoding.UTF8, ct);
    }

    /// <summary>The folder <see cref="ExtractAsync"/> creates for <paramref name="appName"/>.</summary>
    public static string ProjectFolderName(string appName) => $"{SanitizeProjectName(appName)}.Installer";

    private static string GenerateCsproj(TemplateOptions options, string projectName)
    {
        var appProject = options.AppProjectPath ?? $@"..\{projectName}\{projectName}.csproj";

        return $$"""
            <Project Sdk="Microsoft.NET.Sdk">

              <PropertyGroup>
                <OutputType>WinExe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
                <AssemblyName>{{EscapeXml(projectName)}}.Installer</AssemblyName>
                <RootNamespace>{{EscapeXml(projectName)}}.Installer</RootNamespace>
                <Version>1.0.0</Version>

                <!-- Icon for the installer binary itself. Point at the app's .ico. -->
                <!-- <ApplicationIcon>..\{{EscapeXml(projectName)}}\Assets\icon.ico</ApplicationIcon> -->

                <!-- Shipping builds publish AOT; Debug stays JIT for fast iteration. -->
                <PublishAot Condition="'$(Configuration)' == 'Release'">true</PublishAot>
                <SelfContained Condition="'$(Configuration)' == 'Release'">true</SelfContained>

                <!-- 'dotnet run' in Debug skips payload append so the dev loop is fast. -->
                <InstellaEnabled Condition="'$(Configuration)' == 'Debug'">false</InstellaEnabled>
              </PropertyGroup>

              <!-- Instella runtime: compiles into this project's exe. -->
              <ItemGroup>
                <PackageReference Include="Instella.Installer.Runtime" Version="{{EscapeXml(PackageVersion)}}" />
                <PackageReference Include="Instella.Installer.Build" Version="{{EscapeXml(PackageVersion)}}" />
              </ItemGroup>

              <!--
                The app being installed. InstellaPayload=true marks it for the
                publish target; ReferenceOutputAssembly=false + Private=false
                keep the app's DLLs out of the installer's own output (they're
                staged separately and appended as the payload). The last two
                lines keep the build from treating the app as this project's
                own executable reference (it has its own target framework and
                is published separately).
              -->
              <ItemGroup>
                <ProjectReference Include="{{EscapeXml(appProject)}}">
                  <InstellaPayload>true</InstellaPayload>
                  <ReferenceOutputAssembly>false</ReferenceOutputAssembly>
                  <Private>false</Private>
                  <OutputItemType>_InstellaPayloadAssembly</OutputItemType>
                  <SkipGetTargetFrameworkProperties>true</SkipGetTargetFrameworkProperties>
                </ProjectReference>
              </ItemGroup>

            </Project>
            """;
    }

    private static string GenerateProgramCs(TemplateOptions options, string projectName)
    {
        var appId = options.AppId ?? GenerateAppId(options.AppName);
        var serverUrl = options.ServerUrl ?? "https://updates.example.com";
        var keyLine = options.PublisherKey is { Length: > 0 } key
            ? $".WithPublisherKey(\"{EscapeCs(key)}\")"
            : ".WithPublisherKey(\"<paste the public key printed by 'instella keys generate'>\")";

        return $$"""
            using Instella.Core.Manifest;
            using Instella.Installer.Runtime.Builders;

            // The version is the project's <Version> (or `dotnet publish -p:Version=1.2.3`, which is
            // how the CI templates from `instella ci init` pass a release tag's version).
            var version = typeof(Program).Assembly.GetName().Version!;

            return await InstellaInstaller.Create()
                .WithApp("{{EscapeCs(options.AppName)}}", "{{EscapeCs(appId)}}", version)
                .WithServer("{{EscapeCs(serverUrl)}}")
                // Updates are accepted only when signed by this key. Keep the private key
                // secret (CI secret store); consider adding a backup key with a second call.
                {{keyLine}}
                .WithDescription("Installer for {{EscapeCs(options.AppName)}}")
                .WithExecutableName("{{EscapeCs(projectName)}}")
                .WithShortcuts(s => s.Desktop().StartMenu())
                .WithLaunchAfterInstall()
                // Offer a newer published version before installing (hands over to that
                // version's installer, uploaded with `instella upload --installer`).
                .WithNewerVersionPrompt()
                // Let users install another published version: --list-versions,
                // --app-version <v|latest>, --choose-version.
                // .WithVersionSelection()
                .WithElevation(ElevationMode.UserChoice)
                .WithChannel("stable")
                .Build()
                .RunAsync(args);
            """;
    }

    private static string GenerateReadme(TemplateOptions options, string projectName)
    {
        return $"""
            # {options.AppName} Installer

            This project compiles into the installer binary for {options.AppName}.
            `Program.cs` configures the fluent builder; `dotnet publish` produces a
            self-contained installer exe with the app's files appended as payload.

            ## Dev loop (JIT)

            ```bash
            dotnet run
            ```

            `InstellaEnabled` defaults to `false` in Debug, so `dotnet run` skips the
            payload append and just launches the installer wizard against whatever
            state is on your machine. Useful for iterating on custom pages and install
            steps without a full publish cycle.

            ## Building the shipping installer

            ```bash
            # Windows
            dotnet publish -r win-x64 -c Release

            # Linux
            dotnet publish -r linux-x64 -c Release

            # macOS
            dotnet publish -r osx-x64 -c Release
            dotnet publish -r osx-arm64 -c Release
            ```

            The installer exe lands at:

            ```
            bin/Release/net10.0/<rid>/publish/{projectName}.Installer(.exe)
            ```

            The published binary embeds the Instella manifest (generated at build time
            from your `Program.cs` via the `--emit-manifest` CLI flag) plus a zipped
            copy of {projectName}'s publish output as the install payload.

            ## Customizing

            Edit `Program.cs` to add:

            - Custom wizard pages: `.AddPage("welcome", p => ...)`
            - Prerequisites: `.WithPrerequisite(p => p.WithName("...").WithDownloadUrl("...").WithSha256("..."))`
            - File associations: `.WithFileAssociation(".ext", "MyFile", ImageSource.FromFile("..."))`
            - PATH registration: `.WithPathRegistration()`
            - Auto-start: `.WithAutoStart()`
            - Launch the app after the wizard (on by default in this template): `.WithLaunchAfterInstall(checkedByDefault: false)` unticks the box; remove the call to drop it
            - Offer a newer version (on by default in this template): before installing, the installer asks the server whether a newer version was published with `instella upload --installer` and offers to run that version's installer instead; remove `.WithNewerVersionPrompt()` to always install this version
            - Version selection (off by default): uncomment `.WithVersionSelection()` to accept `--list-versions`, `--app-version <version|latest>` and `--choose-version`
            - Windows registry writes: `.OnWindows(w => w.AddRegistryKey(...)...)`
            - Custom install steps: `.AddStep("my-step", sb => sb.Execute(...).Rollback(...))`

            See the Instella.Installer.Runtime docs for the full fluent API surface.

            ## Publishing releases

            `dotnet publish -p:Version=1.2.3` sets the installer's version. Upload the app's files and both
            installers with `instella upload --installer ... --offline-installer ...`, or generate a release
            workflow with `instella ci init` (GitHub or Gitea Actions). See
            {Services.InstellaDocs.Page("publishing.md")}.

            ## Preview the installer (no changes made)

            If the builder chain includes `.EnablePreview()`:

            ```bash
            dotnet run -- --preview
            dotnet run -- --preview --preview-mode=uninstall
            dotnet run -- --preview --preview-fail=extract
            ```
            """;
    }

    internal static string SanitizeProjectName(string appName)
    {
        var sb = new StringBuilder();
        foreach (var c in appName)
        {
            if (char.IsLetterOrDigit(c) || c == '_')
                sb.Append(c);
            // skip spaces, hyphens, and anything non-identifier
        }

        var result = sb.ToString();
        return string.IsNullOrEmpty(result) ? "MyApp" : result;
    }

    private static string GenerateAppId(string appName)
    {
        var sanitized = appName.ToLowerInvariant()
            .Replace(' ', '-')
            .Replace('_', '-');

        var sb = new StringBuilder();
        var lastWasHyphen = false;
        foreach (var c in sanitized)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(c);
                lastWasHyphen = false;
            }
            else if (c == '-' && !lastWasHyphen && sb.Length > 0)
            {
                sb.Append(c);
                lastWasHyphen = true;
            }
        }

        var id = sb.ToString().TrimEnd('-');
        return $"com.example.{(string.IsNullOrEmpty(id) ? "myapp" : id)}";
    }

    /// <summary>
    /// Instella package version referenced by generated projects: the CLI's own version,
    /// so a scaffolded project always matches the tool that created it.
    /// </summary>
    internal static string PackageVersion
    {
        get
        {
            var info = typeof(ProjectTemplate).Assembly
                .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
                .FirstOrDefault()?.InformationalVersion;
            if (string.IsNullOrEmpty(info)) return "0.1.0";
            var plus = info.IndexOf('+');
            return plus >= 0 ? info[..plus] : info;
        }
    }

    private static string EscapeXml(string value) =>
        value.Replace("&", "&amp;")
             .Replace("<", "&lt;")
             .Replace(">", "&gt;")
             .Replace("\"", "&quot;")
             .Replace("'", "&apos;");

    private static string EscapeCs(string value) =>
        value.Replace("\\", "\\\\")
             .Replace("\"", "\\\"");
}

/// <summary>
/// Options supplied to <see cref="ProjectTemplate.ExtractAsync"/>. The
/// resulting project name is derived from <see cref="AppName"/> with
/// spaces, hyphens, and other non-identifier characters stripped.
/// </summary>
public sealed record TemplateOptions
{
    /// <summary>Display name of the application (e.g. "Quick Notes").</summary>
    public required string AppName { get; init; }

    /// <summary>
    /// Reverse-DNS application identifier. When omitted, one is derived from
    /// <see cref="AppName"/> (e.g. "Quick Notes" -> "com.example.quick-notes").
    /// </summary>
    public string? AppId { get; init; }

    /// <summary>
    /// Instella update server URL. When omitted, a placeholder
    /// ("https://updates.example.com") is emitted.
    /// </summary>
    public string? ServerUrl { get; init; }

    /// <summary>
    /// Base64 publisher public key (from <c>instella keys generate</c>). When omitted a
    /// placeholder is emitted, which fails the build until it is replaced.
    /// </summary>
    public string? PublisherKey { get; init; }

    /// <summary>
    /// Relative path from the installer project to the app's .csproj. When
    /// omitted, defaults to <c>..\{SanitizedAppName}\{SanitizedAppName}.csproj</c>.
    /// </summary>
    public string? AppProjectPath { get; init; }
}
