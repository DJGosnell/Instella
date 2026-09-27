using System.CommandLine;
using Instella.CLI.Services;

namespace Instella.CLI.Commands;

/// <summary><c>instella list packages|versions</c>.</summary>
internal static class ListCommand
{
    public static Command Create()
    {
        var list = new Command("list", "List packages or versions");
        list.Subcommands.Add(CreatePackages());
        list.Subcommands.Add(CreateVersions());
        return list;
    }

    private static Command CreatePackages()
    {
        var serverOption = CommonOptions.Server();
        var allowInsecureOption = CommonOptions.AllowInsecure();
        var command = new Command("packages", "List public packages") { serverOption, allowInsecureOption };
        command.SetAction(async (parse, ct) =>
        {
            var server = parse.GetValue(serverOption)!;
            if (!CommonOptions.CheckServerUrl(server, parse.GetValue(allowInsecureOption)))
                return ExitCodes.Usage;

            using var client = new ApiClient(server, "");
            var result = await client.ListPackagesAsync(ct);
            if (!result.IsSuccess || result.Data is null)
            {
                Console.Error.WriteLine($"Error: {result.Error}");
                return ExitCodes.ForStatus(result.StatusCode);
            }

            if (result.Data.Length == 0)
            {
                Console.WriteLine("No packages found.");
                return ExitCodes.Success;
            }

            Console.WriteLine($"{"PACKAGE ID",-30} {"DISPLAY NAME",-30} {"LATEST",-12} {"VERSIONS"}");
            Console.WriteLine(new string('-', 90));
            foreach (var pkg in result.Data)
                Console.WriteLine($"{pkg.PackageId,-30} {pkg.DisplayName,-30} {pkg.LatestVersion ?? "-",-12} {pkg.VersionCount}");
            return ExitCodes.Success;
        });
        return command;
    }

    private static Command CreateVersions()
    {
        var serverOption = CommonOptions.Server();
        var allowInsecureOption = CommonOptions.AllowInsecure();
        var apiKeyOption = CommonOptions.ApiKey();
        var packageOption = new Option<string>("--package", "-p") { Description = "Package ID", Required = true };
        var command = new Command("versions", "List versions of a package (a private package needs an API key that may download it)")
        {
            serverOption, allowInsecureOption, apiKeyOption, packageOption,
        };
        command.SetAction(async (parse, ct) =>
        {
            var server = parse.GetValue(serverOption)!;
            if (!CommonOptions.CheckServerUrl(server, parse.GetValue(allowInsecureOption)))
                return ExitCodes.Usage;

            using var client = new ApiClient(server, CommonOptions.ResolveApiKey(parse.GetValue(apiKeyOption)) ?? "");
            var result = await client.ListVersionsAsync(parse.GetValue(packageOption)!, ct);
            if (!result.IsSuccess || result.Data is null)
            {
                Console.Error.WriteLine($"Error: {result.Error}");
                return ExitCodes.ForStatus(result.StatusCode);
            }

            if (result.Data.Length == 0)
            {
                Console.WriteLine("No versions found.");
                return ExitCodes.Success;
            }

            Console.WriteLine($"{"VERSION",-15} {"CHANNEL",-10} {"RELEASED",-20} {"DOWNLOADS",-12} {"STATUS"}");
            Console.WriteLine(new string('-', 80));
            foreach (var v in result.Data)
                Console.WriteLine($"{v.VersionString,-15} {v.Channel,-10} {v.ReleasedAt,-20:g} {v.DownloadCount,-12} {(v.IsDeprecated ? "Deprecated" : "Active")}");
            return ExitCodes.Success;
        });
        return command;
    }
}
