using System.CommandLine;
using Instella.CLI.Services;

namespace Instella.CLI.Commands;

/// <summary><c>instella delete</c>: delete one version from the server.</summary>
internal static class DeleteCommand
{
    public static Command Create()
    {
        var serverOption = CommonOptions.Server();
        var apiKeyOption = CommonOptions.ApiKey();
        var allowInsecureOption = CommonOptions.AllowInsecure();
        var packageOption = new Option<string>("--package", "-p") { Description = "Package ID", Required = true };
        var versionOption = new Option<string>("--version", "-v") { Description = "Version string", Required = true };
        var yesOption = new Option<bool>("--yes", "-y") { Description = "Skip the confirmation prompt (required when stdin is not interactive)" };

        var command = new Command("delete", "Delete a version from the server")
        {
            serverOption, apiKeyOption, allowInsecureOption, packageOption, versionOption, yesOption,
        };
        command.SetAction(async (parse, ct) =>
        {
            var server = parse.GetValue(serverOption)!;
            if (!CommonOptions.CheckServerUrl(server, parse.GetValue(allowInsecureOption)))
                return ExitCodes.Usage;

            var apiKey = CommonOptions.ResolveApiKey(parse.GetValue(apiKeyOption));
            if (apiKey is null)
            {
                Console.Error.WriteLine("Error: an API key is required. Pass --api-key or set INSTELLA_API_KEY.");
                return ExitCodes.Usage;
            }

            var package = parse.GetValue(packageOption)!;
            var versionText = parse.GetValue(versionOption)!;
            if (!Core.Utilities.AppVersions.TryParse(versionText, out var parsedVersion))
            {
                Console.Error.WriteLine($"Error: invalid version '{versionText}': use numbers like 1.2.3 or 1.2.3.4.");
                return ExitCodes.Usage;
            }
            // The server stores the canonical form: 1.2 and 1.2.0.0 name the same version.
            var version = Core.Utilities.AppVersions.ToCanonicalString(parsedVersion);
            if (!parse.GetValue(yesOption))
            {
                if (Console.IsInputRedirected)
                {
                    Console.Error.WriteLine("Error: refusing to delete without confirmation on non-interactive input. Pass --yes.");
                    return ExitCodes.Usage;
                }
                Console.Write($"Delete {package} v{version}? [y/N] ");
                if (!string.Equals(Console.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("Aborted.");
                    return ExitCodes.Usage;
                }
            }

            Console.WriteLine($"Deleting {package} v{version}...");
            using var client = new ApiClient(server, apiKey);
            var result = await client.DeleteVersionAsync(package, version, ct);
            if (!result.IsSuccess)
            {
                Console.Error.WriteLine(result.StatusCode == System.Net.HttpStatusCode.Forbidden
                    ? "Delete failed: this API key cannot manage versions; create one with 'Manage versions' ticked."
                    : $"Delete failed: {result.Error}");
                return ExitCodes.ForStatus(result.StatusCode);
            }

            Console.WriteLine("Version deleted.");
            return ExitCodes.Success;
        });
        return command;
    }
}
