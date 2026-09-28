using System.CommandLine;
using System.Security.Cryptography;
using System.Text.Json;
using Instella.CLI.Services;
using Instella.Core.Platform;
using Instella.Core.Trust;
using Instella.Core.Wire;

namespace Instella.CLI.Commands;

/// <summary>
/// <c>instella publish</c>: sign and publish a build that CI uploaded with <c>upload --draft</c>.
/// The person who holds the publisher key downloads the draft's unsigned manifest, checks it
/// (against the files they trust with <c>--path</c> / <c>--installer</c>), signs exactly those
/// bytes, and sends the signature; only then do installers and apps see the build.
/// </summary>
internal static class PublishCommand
{
    public static Command Create()
    {
        var serverOption = CommonOptions.Server();
        var apiKeyOption = CommonOptions.ApiKey();
        var allowInsecureOption = CommonOptions.AllowInsecure();
        var packageOption = new Option<string>("--package", "-p") { Description = "Package ID", Required = true };
        var versionOption = new Option<string>("--version", "-v") { Description = "Version of the draft", Required = true };
        var osOption = new Option<string>("--os") { Description = $"Target OS ({string.Join(", ", PlatformStrings.OsNames)})", Required = true };
        var archOption = new Option<string>("--arch") { Description = $"Architecture ({string.Join(", ", PlatformStrings.ArchNames)})", Required = true };
        var pathOption = new Option<DirectoryInfo?>("--path")
        {
            Description = "The app files you expect (for example the CI artifact you reviewed); publishing is refused unless the draft lists exactly these",
        };
        var installerOption = new Option<FileInfo?>("--installer") { Description = "The online installer you expect the draft to list" };
        var offlineInstallerOption = new Option<FileInfo?>("--offline-installer") { Description = "The offline installer you expect the draft to list" };
        var yesOption = new Option<bool>("--yes", "-y") { Description = "Skip the confirmation prompt (required when stdin is not interactive)" };
        var signing = new SigningOptions();

        var command = new Command("publish", "Sign and publish a build uploaded with 'upload --draft'")
        {
            serverOption, apiKeyOption, allowInsecureOption, packageOption, versionOption, osOption, archOption,
            pathOption, installerOption, offlineInstallerOption, yesOption,
        };
        foreach (var option in signing.All) command.Options.Add(option);

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
            if (!Core.Utilities.AppVersions.TryParse(parse.GetValue(versionOption), out var version))
            {
                Console.Error.WriteLine($"Error: invalid version '{parse.GetValue(versionOption)}': use numbers like 1.2.3 or 1.2.3.4.");
                return ExitCodes.Usage;
            }
            if (!PlatformStrings.TryParseOs(parse.GetValue(osOption)!, out var os) || !PlatformStrings.TryParseArch(parse.GetValue(archOption)!, out var arch))
            {
                Console.Error.WriteLine("Error: unknown --os or --arch.");
                return ExitCodes.Usage;
            }

            IReleaseSigningKey? signingKey;
            IDisposable? ownedKey;
            try
            {
                signingKey = signing.Resolve(parse, out ownedKey);
            }
            catch (ArgumentException ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                return ExitCodes.Usage;
            }
            using var _ = ownedKey;
            if (signingKey is null)
            {
                Console.Error.WriteLine($"Error: publishing signs the release. Pass --signing-key (or set {SigningKeyLoader.KeyEnvironmentVariable}) or --sign-command.");
                return ExitCodes.Usage;
            }

            using var client = new ApiClient(server, apiKey);
            var draft = await client.GetDraftAsync(package, version, os, arch, ct);
            if (!draft.IsSuccess)
            {
                Console.Error.WriteLine($"Error: {draft.Error}");
                return ExitCodes.ForStatus(draft.StatusCode);
            }

            var bytes = Convert.FromBase64String(draft.Data!.Manifest);
            ReleaseManifest manifest;
            try
            {
                manifest = JsonSerializer.Deserialize(bytes, TrustJsonContext.Default.ReleaseManifest)
                    ?? throw new JsonException("empty");
            }
            catch (JsonException ex)
            {
                Console.Error.WriteLine($"Error: the draft's manifest cannot be read: {ex.Message}");
                return ExitCodes.Server;
            }

            var problems = Check(manifest, package, version, os, arch,
                parse.GetValue(pathOption), parse.GetValue(installerOption), parse.GetValue(offlineInstallerOption));
            Describe(manifest, draft.Data, signingKey);
            if (problems.Count > 0)
            {
                Console.Error.WriteLine();
                foreach (var p in problems) Console.Error.WriteLine($"Error: {p}");
                Console.Error.WriteLine("Refusing to sign.");
                return ExitCodes.Usage;
            }
            if (parse.GetValue(pathOption) is null)
                Console.WriteLine("Warning: no --path given, so the files were not compared with a copy you trust; " +
                                  "you are signing the list the server holds.");

            if (!parse.GetValue(yesOption))
            {
                if (Console.IsInputRedirected)
                {
                    Console.Error.WriteLine("Error: refusing to publish without confirmation on non-interactive input. Pass --yes.");
                    return ExitCodes.Usage;
                }
                Console.Write($"Sign and publish {package} {version} {PlatformStrings.Os(os)}/{PlatformStrings.Arch(arch)}? [y/N] ");
                if (!string.Equals(Console.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("Aborted.");
                    return ExitCodes.Usage;
                }
            }

            SignedRelease release;
            try
            {
                release = await signingKey.SignAsync(bytes, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine($"Error: signing failed: {ex.Message}");
                return ExitCodes.Signing;
            }

            var result = await client.PublishDraftAsync(package, version, os, arch, release, ct);
            if (!result.IsSuccess)
            {
                Console.Error.WriteLine($"Publish failed: {result.Error}");
                return ExitCodes.ForStatus(result.StatusCode);
            }
            Console.WriteLine(result.Data?.Message ?? "Published.");
            if (ReleaseDescription.PendingMessage(result.Data?.State, result.Data?.PublishAfter) is { } pending)
                Console.WriteLine(pending);
            return ExitCodes.Success;
        });
        return command;
    }

    /// <summary>What does not match: identity, and the local files and installers when given.</summary>
    internal static List<string> Check(ReleaseManifest manifest, string package, Version version, TargetPlatform os, Architecture arch,
        DirectoryInfo? path, FileInfo? installer, FileInfo? offlineInstaller)
    {
        var problems = new List<string>();
        if (manifest.AppId != package) problems.Add($"the draft is for '{manifest.AppId}', not '{package}'");
        if (!Core.Utilities.AppVersions.Equal(manifest.Version, version)) problems.Add($"the draft is version {manifest.Version}, not {version}");
        if (manifest.Os != PlatformStrings.Os(os) || manifest.Arch != PlatformStrings.Arch(arch))
            problems.Add($"the draft is for {manifest.Os}/{manifest.Arch}");

        if (path is not null)
        {
            if (!path.Exists)
            {
                problems.Add($"--path '{path.FullName}' does not exist");
            }
            else
            {
                var local = Directory.EnumerateFiles(path.FullName, "*", SearchOption.AllDirectories)
                    .ToDictionary(f => Path.GetRelativePath(path.FullName, f).Replace('\\', '/'), f => f, StringComparer.Ordinal);
                var listed = manifest.Files.ToDictionary(f => f.Path, StringComparer.Ordinal);
                foreach (var missing in listed.Keys.Except(local.Keys).Take(5))
                    problems.Add($"the draft lists '{missing}', which is not in --path");
                foreach (var extra in local.Keys.Except(listed.Keys).Take(5))
                    problems.Add($"'{extra}' in --path is not in the draft");
                foreach (var (rel, file) in local.Where(kv => listed.ContainsKey(kv.Key)))
                {
                    var entry = listed[rel];
                    if (new FileInfo(file).Length != entry.Size || !string.Equals(Sha(file), entry.Sha256, StringComparison.OrdinalIgnoreCase))
                        problems.Add($"'{rel}' differs from the draft (size or SHA-256)");
                }
            }
        }

        foreach (var (kind, file) in new[] { (InstallerKinds.Online, installer), (InstallerKinds.Offline, offlineInstaller) })
        {
            if (file is null) continue;
            var entry = manifest.Installers?.FirstOrDefault(i => i.Kind == kind);
            if (entry is null) problems.Add($"the draft lists no {kind} installer");
            else if (!file.Exists) problems.Add($"{kind} installer '{file.FullName}' does not exist");
            else if (file.Length != entry.Size || !string.Equals(Sha(file.FullName), entry.Sha256, StringComparison.OrdinalIgnoreCase))
                problems.Add($"the {kind} installer differs from the draft's '{entry.FileName}'");
        }
        return problems;
    }

    private static void Describe(ReleaseManifest manifest, DraftResponse draft, IReleaseSigningKey key)
    {
        ReleaseDescription.Write("Draft", manifest, draft.UploadedAt, draft.Changelog, Console.Out);
        Console.WriteLine($"  Signing with key {key.PublicKey.KeyId}{(key is CommandSigningKey ? " (sign command)" : "")}");
    }

    private static string Sha(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
