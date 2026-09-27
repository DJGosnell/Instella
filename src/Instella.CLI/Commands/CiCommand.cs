using System.CommandLine;
using System.Text;
using Instella.CLI.Templates;

namespace Instella.CLI.Commands;

/// <summary><c>instella ci init</c>: writes a release workflow for GitHub or Gitea Actions.</summary>
internal static class CiCommand
{
    public static Command Create()
    {
        var hostOption = new Option<string>("--host") { Description = "github or gitea", Required = true };
        var signingOption = new Option<string>("--signing")
        {
            Description = "How releases are signed: kms-azure, kms-aws, kms-gcp (a key that never leaves the key service), " +
                          "draft (CI uploads, a person runs 'instella publish'), or secret (the PEM key as a CI secret)",
            Required = true,
        };
        var appProjectOption = new Option<string>("--app-project") { Description = "The app's .csproj, relative to the repository root", Required = true };
        var installerProjectOption = new Option<string>("--installer-project") { Description = "The installer's .csproj, relative to the repository root", Required = true };
        var packageOption = new Option<string>("--package", "-p") { Description = "Package ID on the server (the installer's app id)", Required = true };
        var serverOption = new Option<string>("--server", "-s") { Description = "Update server URL", Required = true };
        var nameOption = new Option<string?>("--name", "-n") { Description = "Name in the installer file names (default: the app project's name)" };
        var ridOption = new Option<string>("--rid") { Description = "Runtime identifier to build for", DefaultValueFactory = _ => "win-x64" };
        var outputOption = new Option<string?>("--output", "-o") { Description = "Repository root (default: current directory)" };
        var forceOption = new Option<bool>("--force") { Description = "Overwrite an existing workflow file" };

        var init = new Command("init", "Write a release workflow (build, then upload signed) for GitHub or Gitea Actions")
        {
            hostOption, signingOption, appProjectOption, installerProjectOption, packageOption, serverOption,
            nameOption, ridOption, outputOption, forceOption,
        };
        init.SetAction(async (parse, ct) =>
        {
            CiHost host;
            switch (parse.GetValue(hostOption)!.ToLowerInvariant())
            {
                case "github": host = CiHost.GitHub; break;
                case "gitea": host = CiHost.Gitea; break;
                default:
                    Console.Error.WriteLine("Error: --host must be github or gitea.");
                    return ExitCodes.Usage;
            }
            if (!CiTemplate.TryParseSigning(parse.GetValue(signingOption)!, out var signing))
            {
                Console.Error.WriteLine("Error: --signing must be kms-azure, kms-aws, kms-gcp, draft or secret.");
                return ExitCodes.Usage;
            }
            var server = parse.GetValue(serverOption)!;
            if (!CommonOptions.CheckServerUrl(server, allowInsecure: false))
                return ExitCodes.Usage;

            var appProject = parse.GetValue(appProjectOption)!.Replace('\\', '/');
            var options = new CiOptions
            {
                Host = host,
                Signing = signing,
                AppProject = appProject,
                InstallerProject = parse.GetValue(installerProjectOption)!.Replace('\\', '/'),
                PackageId = parse.GetValue(packageOption)!,
                ServerUrl = server,
                AppName = parse.GetValue(nameOption) ?? Path.GetFileNameWithoutExtension(appProject),
                Rid = parse.GetValue(ridOption)!,
            };

            var root = parse.GetValue(outputOption) ?? Directory.GetCurrentDirectory();
            var path = Path.Combine(root, CiTemplate.WorkflowPath(host).Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path) && !parse.GetValue(forceOption))
            {
                Console.Error.WriteLine($"Error: {path} exists. Pass --force to overwrite it.");
                return ExitCodes.Usage;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, CiTemplate.Generate(options), new UTF8Encoding(false), ct);
            Console.WriteLine(CiTemplate.SetupNotes(options));
            return ExitCodes.Success;
        });

        return new Command("ci", "Continuous-integration helpers") { init };
    }
}
