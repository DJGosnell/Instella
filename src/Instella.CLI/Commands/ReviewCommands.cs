using System.CommandLine;
using System.Security.Cryptography;
using System.Text.Json;
using Instella.CLI.Services;
using Instella.Core.Platform;
using Instella.Core.Trust;
using Instella.Core.Wire;

namespace Instella.CLI.Commands;

/// <summary>One platform build of one version of a package.</summary>
internal sealed record ReleaseTarget(string Package, Version Version, TargetPlatform Os, Architecture Arch)
{
    public override string ToString() => $"{Package} {Version} {PlatformStrings.Os(Os)}/{PlatformStrings.Arch(Arch)}";
}

/// <summary>Where a command reads and writes; the process console, or buffers in tests.</summary>
internal sealed record CommandConsole(TextWriter Out, TextWriter Error, TextReader In, bool InputRedirected)
{
    public static CommandConsole System => new(Console.Out, Console.Error, Console.In, Console.IsInputRedirected);
}

/// <summary>
/// The options <c>approve</c> and <c>reject</c> share: the server, an API key with the approve permission,
/// and the build (package, version, OS, architecture).
/// </summary>
internal sealed class ReviewOptions
{
    public Option<string> Server { get; } = CommonOptions.Server();
    public Option<string?> ApiKey { get; } = CommonOptions.ApiKey();
    public Option<bool> AllowInsecure { get; } = CommonOptions.AllowInsecure();
    public Option<string> Package { get; } = new("--package", "-p") { Description = "Package ID", Required = true };
    public Option<string> Version { get; } = new("--version", "-v") { Description = "Version of the release", Required = true };
    public Option<string> Os { get; } = new("--os") { Description = $"Target OS ({string.Join(", ", PlatformStrings.OsNames)})", Required = true };
    public Option<string> Arch { get; } = new("--arch") { Description = $"Architecture ({string.Join(", ", PlatformStrings.ArchNames)})", Required = true };
    public Option<bool> Yes { get; } = new("--yes", "-y") { Description = "Skip the confirmation prompt (required when stdin is not interactive)" };

    public IEnumerable<Option> All => [Server, ApiKey, AllowInsecure, Package, Version, Os, Arch, Yes];

    /// <summary>The client and the build, or an exit code after writing why not.</summary>
    public bool TryResolve(ParseResult parse, out ApiClient? client, out ReleaseTarget? target, out int exit)
    {
        client = null;
        target = null;
        exit = ExitCodes.Usage;
        var server = parse.GetValue(Server)!;
        if (!CommonOptions.CheckServerUrl(server, parse.GetValue(AllowInsecure)))
            return false;
        var apiKey = CommonOptions.ResolveApiKey(parse.GetValue(ApiKey));
        if (apiKey is null)
        {
            Console.Error.WriteLine("Error: an API key is required. Pass --api-key or set INSTELLA_API_KEY (a key with the approve permission).");
            return false;
        }
        if (!Core.Utilities.AppVersions.TryParse(parse.GetValue(Version), out var version))
        {
            Console.Error.WriteLine($"Error: invalid version '{parse.GetValue(Version)}': use numbers like 1.2.3 or 1.2.3.4.");
            return false;
        }
        if (!PlatformStrings.TryParseOs(parse.GetValue(Os)!, out var os) || !PlatformStrings.TryParseArch(parse.GetValue(Arch)!, out var arch))
        {
            Console.Error.WriteLine("Error: unknown --os or --arch.");
            return false;
        }
        client = new ApiClient(server, apiKey);
        target = new ReleaseTarget(parse.GetValue(Package)!, version, os, arch);
        return true;
    }
}

/// <summary>
/// <c>instella approve</c>: publishes a release the server holds back (release approval Delayed or
/// Required). Shows what was uploaded, optionally compares it with files you trust, asks, then approves
/// exactly the manifest it showed. Needs an API key with the approve permission that did not upload it.
/// </summary>
internal static class ApproveCommand
{
    public static Command Create()
    {
        var options = new ReviewOptions();
        var pathOption = new Option<DirectoryInfo?>("--path")
        {
            Description = "The app files you expect (for example the CI artifact); approval is refused unless the release lists exactly these",
        };
        var installerOption = new Option<FileInfo?>("--installer") { Description = "The online installer you expect the release to list" };
        var offlineInstallerOption = new Option<FileInfo?>("--offline-installer") { Description = "The offline installer you expect the release to list" };

        var command = new Command("approve", "Approve a release that is pending on the server, so installations can update to it");
        foreach (var option in options.All) command.Options.Add(option);
        command.Options.Add(pathOption);
        command.Options.Add(installerOption);
        command.Options.Add(offlineInstallerOption);

        command.SetAction(async (parse, ct) =>
        {
            if (!options.TryResolve(parse, out var client, out var target, out var exit))
                return exit;
            using (client)
                return await RunAsync(client!, target!, parse.GetValue(pathOption), parse.GetValue(installerOption),
                    parse.GetValue(offlineInstallerOption), parse.GetValue(options.Yes), CommandConsole.System, ct);
        });
        return command;
    }

    internal static async Task<int> RunAsync(ApiClient client, ReleaseTarget target, DirectoryInfo? path, FileInfo? installer,
        FileInfo? offlineInstaller, bool yes, CommandConsole console, CancellationToken ct)
    {
        var release = await Review.FetchAsync(client, target, console, ct);
        if (release.Exit is { } failed) return failed;
        var (summary, manifest, hash) = (release.Response!.Summary, release.Manifest!, release.Hash!);

        if (summary.State != ReleaseStates.Pending)
        {
            console.Error.WriteLine($"Error: {target} is a draft; sign it with 'instella publish' first.");
            return ExitCodes.Usage;
        }

        var problems = PublishCommand.Check(manifest, target.Package, target.Version, target.Os, target.Arch, path, installer, offlineInstaller);
        Review.Describe("Pending", release.Response, manifest, console.Out);
        if (problems.Count > 0)
        {
            console.Error.WriteLine();
            foreach (var p in problems) console.Error.WriteLine($"Error: {p}");
            console.Error.WriteLine("Refusing to approve.");
            return ExitCodes.Usage;
        }
        if (path is null)
            console.Out.WriteLine("Warning: no --path given, so the files were not compared with a copy you trust; " +
                                  "you are approving the list the server holds.");

        if (!Review.Confirm($"Approve {target}? Installations can update to it immediately. [y/N] ", yes, "approve", console))
            return ExitCodes.Usage;

        var result = await client.ApproveAsync(target.Package, target.Version, target.Os, target.Arch, hash, ct);
        if (!result.IsSuccess)
        {
            console.Error.WriteLine($"Approve failed: {result.Error}");
            return ExitCodes.ForStatus(result.StatusCode);
        }
        console.Out.WriteLine(result.Data?.Message ?? "Approved.");
        return ExitCodes.Success;
    }
}

/// <summary>
/// <c>instella reject</c>: deletes a release that is pending on the server, or a draft. The version
/// number can then be uploaded again. Needs an API key with the approve permission.
/// </summary>
internal static class RejectCommand
{
    public static Command Create()
    {
        var options = new ReviewOptions();
        var reasonOption = new Option<string?>("--reason") { Description = "Why, recorded in the server's security log" };

        var command = new Command("reject", "Reject (delete) a release pending on the server, or a draft");
        foreach (var option in options.All) command.Options.Add(option);
        command.Options.Add(reasonOption);

        command.SetAction(async (parse, ct) =>
        {
            if (!options.TryResolve(parse, out var client, out var target, out var exit))
                return exit;
            using (client)
                return await RunAsync(client!, target!, parse.GetValue(reasonOption), parse.GetValue(options.Yes), CommandConsole.System, ct);
        });
        return command;
    }

    internal static async Task<int> RunAsync(ApiClient client, ReleaseTarget target, string? reason, bool yes, CommandConsole console,
        CancellationToken ct)
    {
        var release = await Review.FetchAsync(client, target, console, ct);
        if (release.Exit is { } failed) return failed;

        Review.Describe(release.Response!.Summary.State == ReleaseStates.Draft ? "Draft" : "Pending", release.Response, release.Manifest!, console.Out);
        if (!Review.Confirm($"Reject {target}? The build is deleted. [y/N] ", yes, "reject", console))
            return ExitCodes.Usage;

        var result = await client.RejectAsync(target.Package, target.Version, target.Os, target.Arch, release.Hash, reason, ct);
        if (!result.IsSuccess)
        {
            console.Error.WriteLine($"Reject failed: {result.Error}");
            return ExitCodes.ForStatus(result.StatusCode);
        }
        console.Out.WriteLine(result.Data?.Message ?? "Rejected.");
        return ExitCodes.Success;
    }
}

/// <summary><c>instella pending</c>: lists a package's releases pending approval and its drafts.</summary>
internal static class PendingCommand
{
    public static Command Create()
    {
        var serverOption = CommonOptions.Server();
        var apiKeyOption = CommonOptions.ApiKey();
        var allowInsecureOption = CommonOptions.AllowInsecure();
        var packageOption = new Option<string>("--package", "-p") { Description = "Package ID", Required = true };

        var command = new Command("pending", "List a package's releases pending approval, and its drafts")
        {
            serverOption, apiKeyOption, allowInsecureOption, packageOption,
        };
        command.SetAction(async (parse, ct) =>
        {
            var server = parse.GetValue(serverOption)!;
            if (!CommonOptions.CheckServerUrl(server, parse.GetValue(allowInsecureOption)))
                return ExitCodes.Usage;
            var apiKey = CommonOptions.ResolveApiKey(parse.GetValue(apiKeyOption));
            if (apiKey is null)
            {
                Console.Error.WriteLine("Error: an API key is required. Pass --api-key or set INSTELLA_API_KEY (a key with the approve permission).");
                return ExitCodes.Usage;
            }
            using var client = new ApiClient(server, apiKey);
            return await RunAsync(client, parse.GetValue(packageOption)!, CommandConsole.System, ct);
        });
        return command;
    }

    internal static async Task<int> RunAsync(ApiClient client, string package, CommandConsole console, CancellationToken ct)
    {
        var result = await client.ListUnpublishedAsync(package, ct);
        if (!result.IsSuccess)
        {
            console.Error.WriteLine($"Error: {result.Error}");
            return ExitCodes.ForStatus(result.StatusCode);
        }
        if (result.Data!.Length == 0)
        {
            console.Out.WriteLine($"Nothing awaits a decision for {package}.");
            return ExitCodes.Success;
        }
        foreach (var r in result.Data)
        {
            var state = r.State == ReleaseStates.Draft ? "draft (unsigned)"
                : r.PublishAfter is { } at ? $"pending, publishes at {at:u}" : "pending approval";
            var key = r.KeyId is null ? "" : $", signed by {r.KeyLabel ?? r.KeyId}";
            console.Out.WriteLine($"{r.Version,-12} {r.Os}/{r.Arch,-8} {state}; uploaded {r.UploadedAt:u} by {r.UploadedBy ?? "?"}{key}");
        }
        return ExitCodes.Success;
    }
}

/// <summary>Fetching, showing and confirming a release for <c>approve</c> and <c>reject</c>.</summary>
internal static class Review
{
    internal sealed record Fetched(int? Exit, UnpublishedReleaseResponse? Response = null, ReleaseManifest? Manifest = null, string? Hash = null);

    /// <summary>The release, its parsed manifest and the SHA-256 of its bytes; or the exit code after writing why not.</summary>
    public static async Task<Fetched> FetchAsync(ApiClient client, ReleaseTarget target, CommandConsole console, CancellationToken ct)
    {
        var response = await client.GetUnpublishedAsync(target.Package, target.Version, target.Os, target.Arch, ct);
        if (!response.IsSuccess)
        {
            console.Error.WriteLine($"Error: {response.Error}");
            return new(ExitCodes.ForStatus(response.StatusCode));
        }
        try
        {
            var bytes = Convert.FromBase64String(response.Data!.Manifest);
            var manifest = JsonSerializer.Deserialize(bytes, TrustJsonContext.Default.ReleaseManifest) ?? throw new JsonException("empty");
            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            if (response.Data.Summary.ManifestSha256 is { } claimed && !string.Equals(claimed, hash, StringComparison.OrdinalIgnoreCase))
            {
                console.Error.WriteLine("Error: the server's manifest hash does not match the manifest it sent.");
                return new(ExitCodes.Server);
            }
            return new(null, response.Data, manifest, hash);
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            console.Error.WriteLine($"Error: the release manifest cannot be read: {ex.Message}");
            return new(ExitCodes.Server);
        }
    }

    public static void Describe(string title, UnpublishedReleaseResponse response, ReleaseManifest manifest, TextWriter output)
    {
        var s = response.Summary;
        ReleaseDescription.Write(title, manifest, s.UploadedAt, response.Changelog, output);
        output.WriteLine($"  Signed by: {(s.KeyId is null ? "not signed yet" : s.KeyLabel is null ? s.KeyId : $"{s.KeyId} ({s.KeyLabel})")}");
        output.WriteLine($"  Uploaded by API key: {s.UploadedBy ?? "unknown"}");
        if (s.PublishAfter is { } at)
            output.WriteLine($"  Publishes automatically at {at:u} unless rejected");
    }

    /// <summary>True with <paramref name="yes"/>, or after a "y" on interactive input.</summary>
    public static bool Confirm(string prompt, bool yes, string verb, CommandConsole console)
    {
        if (yes) return true;
        if (console.InputRedirected)
        {
            console.Error.WriteLine($"Error: refusing to {verb} without confirmation on non-interactive input. Pass --yes.");
            return false;
        }
        console.Out.Write(prompt);
        if (string.Equals(console.In.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase))
            return true;
        console.Out.WriteLine("Aborted.");
        return false;
    }
}
