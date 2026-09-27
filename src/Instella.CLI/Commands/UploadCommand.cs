using System.CommandLine;
using Instella.CLI.Services;
using Instella.Core.Platform;
using Instella.Core.Trust;
using Instella.Core.Wire;

namespace Instella.CLI.Commands;

/// <summary><c>instella upload</c>: session upload of a publish directory, signed by the publisher key.</summary>
internal static class UploadCommand
{
    public static Command Create()
    {
        var serverOption = CommonOptions.Server();
        var apiKeyOption = CommonOptions.ApiKey();
        var allowInsecureOption = CommonOptions.AllowInsecure();
        var packageOption = new Option<string>("--package", "-p") { Description = "Package ID", Required = true };
        var versionOption = new Option<string>("--version", "-v") { Description = "Version (e.g. 1.0.0)", Required = true };
        var pathOption = new Option<DirectoryInfo>("--path") { Description = "Published files directory", Required = true };
        var osOption = new Option<string?>("--os") { Description = $"Target OS ({string.Join(", ", PlatformStrings.OsNames)}); detected from a RID in --path when omitted" };
        var archOption = new Option<string?>("--arch") { Description = $"Architecture ({string.Join(", ", PlatformStrings.ArchNames)}); detected from a RID in --path when omitted" };
        var channelOption = new Option<string>("--channel", "-c") { Description = "Release channel", DefaultValueFactory = _ => "stable" };
        var changelogOption = new Option<string?>("--changelog") { Description = "Changelog (markdown)" };
        var signing = new SigningOptions();
        var unsignedOption = new Option<bool>("--unsigned") { Description = "Upload without a publisher signature (development only)" };
        var draftOption = new Option<bool>("--draft")
        {
            Description = "Upload unsigned and keep the build hidden until 'instella publish' signs it (CI without the key)",
        };
        var trustedKeyOption = new Option<string[]>("--trusted-key")
        {
            Description = "Key rotation: a publisher public key (base64, as 'keys show' prints) that installations trust after " +
                "taking this release. Repeat for each key; the list replaces the installation's trusted keys, so include every key to keep.",
            AllowMultipleArgumentsPerToken = false,
        };

        var installerOption = new Option<FileInfo?>("--installer")
        {
            Description = "The online installer built for this version and platform; published with the build and listed in the signed release",
        };
        var offlineInstallerOption = new Option<FileInfo?>("--offline-installer")
        {
            Description = "The offline installer (carries the app) built for this version and platform",
        };

        var command = new Command("upload", "Upload a build to the server")
        {
            serverOption, apiKeyOption, allowInsecureOption, packageOption, versionOption, pathOption,
            osOption, archOption, channelOption, changelogOption, unsignedOption, trustedKeyOption,
            installerOption, offlineInstallerOption, draftOption,
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

            var directory = parse.GetValue(pathOption)!;
            if (!directory.Exists)
            {
                Console.Error.WriteLine($"Error: directory not found: {directory.FullName}");
                return ExitCodes.Usage;
            }

            var versionText = parse.GetValue(versionOption)!;
            // 1.2, 1.2.0 and 1.2.0.0 are one release: the builder normalizes the same way.
            if (!Core.Utilities.AppVersions.TryParse(versionText, out var version))
            {
                Console.Error.WriteLine($"Error: invalid version '{versionText}': use numbers like 1.2.3 or 1.2.3.4.");
                return ExitCodes.Usage;
            }

            var (detectedOs, detectedArch) = TryDetectRidFromPath(directory.FullName);
            if (!ResolvePlatform(parse.GetValue(osOption), detectedOs, out var platform) ||
                !ResolveArchitecture(parse.GetValue(archOption), detectedArch, out var architecture))
                return ExitCodes.Usage;

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
            var draft = parse.GetValue(draftOption);
            if (draft && (signingKey is not null || parse.GetValue(unsignedOption)))
            {
                Console.Error.WriteLine("Error: --draft uploads the release unsigned for 'instella publish' to sign; " +
                    "do not pass (or set) a signing key or --unsigned with it.");
                return ExitCodes.Usage;
            }
            if (signingKey is null && !parse.GetValue(unsignedOption) && !draft)
            {
                Console.Error.WriteLine(
                    $"Error: no signing key. Pass --signing-key (or set {SigningKeyLoader.KeyEnvironmentVariable}) or --sign-command; " +
                    "installations only accept updates signed by a key they trust. Pass --unsigned to upload anyway (development only).");
                return ExitCodes.Usage;
            }

            if (!TryParseTrustedKeys(parse.GetValue(trustedKeyOption) ?? [], out var rotateToKeys))
                return ExitCodes.Usage;
            if (rotateToKeys is not null && signingKey is null && !draft)
            {
                Console.Error.WriteLine("Error: --trusted-key needs a signing key; an unsigned release cannot rotate keys.");
                return ExitCodes.Usage;
            }

            var installers = new List<(string Kind, FileInfo File)>();
            foreach (var (kind, file) in new[] { (InstallerKinds.Online, parse.GetValue(installerOption)), (InstallerKinds.Offline, parse.GetValue(offlineInstallerOption)) })
            {
                if (file is null) continue;
                if (!file.Exists)
                {
                    Console.Error.WriteLine($"Error: {kind} installer not found: {file.FullName}");
                    return ExitCodes.Usage;
                }
                if (!InstallerKinds.IsValidFileName(file.Name))
                {
                    Console.Error.WriteLine($"Error: installer file name '{file.Name}' is not allowed: use letters, digits, " +
                        $"'. _ - + ( )' and spaces, at most {InstallerKinds.MaxFileNameLength} characters.");
                    return ExitCodes.Usage;
                }
                installers.Add((kind, file));
            }

            var files = directory.GetFiles("*", SearchOption.AllDirectories);
            if (!ChannelNames.TryNormalize(parse.GetValue(channelOption), out var channel))
            {
                Console.Error.WriteLine($"Error: invalid --channel '{parse.GetValue(channelOption)}': {ChannelNames.Rule}.");
                return ExitCodes.Usage;
            }
            Console.WriteLine($"Uploading to {server}...");
            Console.WriteLine($"  Package: {parse.GetValue(packageOption)}");
            Console.WriteLine($"  Version: {version}");
            Console.WriteLine($"  Platform: {PlatformStrings.Os(platform)}/{PlatformStrings.Arch(architecture)}");
            Console.WriteLine($"  Channel: {channel}");
            Console.WriteLine($"  Files: {files.Length} ({FormatSize(files.Sum(f => f.Length))})");
            foreach (var (kind, file) in installers)
                Console.WriteLine($"  {char.ToUpperInvariant(kind[0])}{kind[1..]} installer: {file.Name} ({FormatSize(file.Length)})");
            Console.WriteLine($"  Signed: {(draft ? "no: draft, hidden until 'instella publish'" : signingKey is null ? "NO (--unsigned)" : $"yes, key {signingKey.PublicKey.KeyId}{(signingKey is CommandSigningKey ? " (sign command)" : "")}")}");
            if (rotateToKeys is not null)
            {
                Console.WriteLine($"  Rotates trusted keys to: {string.Join(", ", rotateToKeys.Select(k => k.KeyId))}");
                if (!rotateToKeys.Any(k => k.KeyId == signingKey!.PublicKey.KeyId))
                    Console.WriteLine("  Note: the signing key is not in that list, so installations stop trusting it after this release.");
            }
            Console.WriteLine();

            using var client = new UploadClient(server, apiKey);
            var lastPercent = -1;
            var progress = new Progress<UploadProgress>(p =>
            {
                var percent = p.TotalFiles > 0 ? (int)(100.0 * p.FilesUploaded / p.TotalFiles) : 100;
                if (percent == lastPercent) return;
                Console.Write($"\rUploading: {percent}% ({p.FilesUploaded}/{p.TotalFiles} files)");
                lastPercent = percent;
            });

            var result = await client.UploadVersionAsync(new UploadRequest
            {
                PackageId = parse.GetValue(packageOption)!,
                Version = version,
                SourceDirectory = directory.FullName,
                Channel = channel,
                Platform = platform,
                Architecture = architecture,
                Changelog = parse.GetValue(changelogOption),
                SignWith = signingKey is null ? null : signingKey.SignAsync,
                RotateToKeys = rotateToKeys,
                Installers = installers.Select(i => new InstallerUpload(i.Kind, i.File.FullName)).ToList(),
                Draft = draft,
            }, progress, ct);
            Console.WriteLine();

            if (!result.Success)
            {
                Console.Error.WriteLine($"Upload failed: {result.Error}");
                return result.SigningFailed ? ExitCodes.Signing : ExitCodes.ForStatus(result.StatusCode);
            }

            Console.WriteLine(draft
                ? $"Draft uploaded. Publish it with: instella publish --server {server} --package {parse.GetValue(packageOption)} " +
                  $"--version {version} --os {PlatformStrings.Os(platform)} --arch {PlatformStrings.Arch(architecture)} --path <the same files>"
                : "Upload successful.");
            if (!string.IsNullOrEmpty(result.VersionUrl))
                Console.WriteLine($"Version URL: {result.VersionUrl}");
            return ExitCodes.Success;
        });
        return command;
    }

    /// <summary>Parses <c>--trusted-key</c> values; <paramref name="keys"/> is null when none were given.</summary>
    internal static bool TryParseTrustedKeys(IReadOnlyList<string> values, out List<PublisherKey>? keys)
    {
        keys = null;
        if (values.Count == 0) return true;
        var parsed = new List<PublisherKey>(values.Count);
        foreach (var value in values)
        {
            PublisherKey key;
            try
            {
                key = KeyIds.FromPublicKey(value);
            }
            catch (ArgumentException ex)
            {
                Console.Error.WriteLine($"Error: --trusted-key '{value}': {ex.Message}");
                return false;
            }
            if (parsed.All(k => k.KeyId != key.KeyId)) parsed.Add(key);
        }
        keys = parsed;
        return true;
    }

    private static bool ResolvePlatform(string? value, TargetPlatform? detected, out TargetPlatform platform)
    {
        if (value is not null)
        {
            if (PlatformStrings.TryParseOs(value, out platform)) return true;
            Console.Error.WriteLine($"Error: unknown OS '{value}'. Valid values: {string.Join(", ", PlatformStrings.OsNames)}.");
            return false;
        }
        if (detected is { } d)
        {
            platform = d;
            Console.WriteLine($"Detected OS: {PlatformStrings.Os(d)}");
            return true;
        }
        platform = default;
        Console.Error.WriteLine("Error: could not detect the target OS from --path. Pass --os.");
        return false;
    }

    private static bool ResolveArchitecture(string? value, Architecture? detected, out Architecture architecture)
    {
        if (value is not null)
        {
            if (PlatformStrings.TryParseArch(value, out architecture)) return true;
            Console.Error.WriteLine($"Error: unknown architecture '{value}'. Valid values: {string.Join(", ", PlatformStrings.ArchNames)}.");
            return false;
        }
        if (detected is { } d)
        {
            architecture = d;
            Console.WriteLine($"Detected architecture: {PlatformStrings.Arch(d)}");
            return true;
        }
        architecture = default;
        Console.Error.WriteLine("Error: could not detect the target architecture from --path. Pass --arch.");
        return false;
    }

    /// <summary>Finds a RID segment such as <c>win-x64</c> or <c>osx-arm64</c> in a path.</summary>
    internal static (TargetPlatform? Os, Architecture? Arch) TryDetectRidFromPath(string path)
    {
        foreach (var part in path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Reverse())
        {
            var rid = part.ToLowerInvariant().Split('-');
            if (rid.Length < 2) continue;
            if (PlatformStrings.TryParseOs(rid[0], out var os) && PlatformStrings.TryParseArch(rid[1], out var arch))
                return (os, arch);
        }
        return (null, null);
    }

    internal static string FormatSize(long bytes) => bytes switch
    {
        >= 1_073_741_824 => $"{bytes / 1_073_741_824.0:F1} GB",
        >= 1_048_576 => $"{bytes / 1_048_576.0:F1} MB",
        >= 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes} B",
    };
}
