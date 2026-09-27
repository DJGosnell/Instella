using System.CommandLine;
using Instella.CLI.Templates;

namespace Instella.CLI.Commands;

/// <summary><c>instella init</c>: scaffold an installer project.</summary>
internal static class InitCommand
{
    public static Command Create()
    {
        var nameOption = new Option<string?>("--name", "-n") { Description = "Application name" };
        var outputOption = new Option<string?>("--output", "-o") { Description = "Output directory (default: current)" };
        var appIdOption = new Option<string?>("--app-id") { Description = "Application ID (default: derived from name)" };
        var serverOption = new Option<string?>("--server") { Description = "Update server URL (default: placeholder)" };
        var publisherKeyOption = new Option<string?>("--publisher-key") { Description = "Publisher public key from 'instella keys generate'" };
        var appOption = new Option<FileInfo?>("--app") { Description = "The app's project (.csproj) to install (default: ../{Name}/{Name}.csproj)" };

        var command = new Command("init", "Initialize a new installer project")
        {
            nameOption, outputOption, appIdOption, serverOption, publisherKeyOption, appOption,
        };
        command.SetAction(async (parse, ct) =>
        {
            var name = parse.GetValue(nameOption);
            var output = parse.GetValue(outputOption) ?? Directory.GetCurrentDirectory();

            if (string.IsNullOrWhiteSpace(name))
            {
                if (Console.IsInputRedirected)
                {
                    Console.Error.WriteLine("Error: --name is required on non-interactive input.");
                    return ExitCodes.Usage;
                }
                Console.Write("Application name: ");
                name = Console.ReadLine();
                if (string.IsNullOrWhiteSpace(name))
                {
                    Console.Error.WriteLine("Error: application name is required.");
                    return ExitCodes.Usage;
                }
            }

            Console.WriteLine($"Creating installer project for '{name}'...");
            Console.WriteLine($"  Output: {output}");
            try
            {
                await ProjectTemplate.ExtractAsync(output, new TemplateOptions
                {
                    AppName = name,
                    AppId = parse.GetValue(appIdOption),
                    ServerUrl = parse.GetValue(serverOption),
                    PublisherKey = parse.GetValue(publisherKeyOption),
                    // The reference is written relative to the installer project.
                    AppProjectPath = parse.GetValue(appOption) is { } app
                        ? Path.GetRelativePath(Path.Combine(Path.GetFullPath(output), ProjectTemplate.ProjectFolderName(name)), app.FullName)
                        : null,
                }, ct);
            }
            catch (IOException ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                return ExitCodes.Usage;
            }

            Console.WriteLine();
            Console.WriteLine("Project created. Next steps:");
            var step = 1;
            Console.WriteLine($"  {step++}. cd {ProjectTemplate.ProjectFolderName(name)} and check the ProjectReference to your app");
            if (parse.GetValue(publisherKeyOption) is null)
            {
                // Outside the repository: a key committed by accident can sign anyone's update.
                var keyPath = OperatingSystem.IsWindows()
                    ? $@"%USERPROFILE%\.instella\keys\{ProjectTemplate.SanitizeProjectName(name)}.pem"
                    : $"~/.instella/keys/{ProjectTemplate.SanitizeProjectName(name)}.pem";
                Console.WriteLine($"  {step++}. Run 'instella keys generate --out \"{keyPath}\"' and paste the public key into WithPublisherKey(...)");
            }
            Console.WriteLine($"  {step}. Run 'dotnet publish -r win-x64 -c Release' to build the installer");
            return ExitCodes.Success;
        });
        return command;
    }
}
