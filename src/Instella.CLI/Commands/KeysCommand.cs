using System.CommandLine;
using Instella.CLI.Services;
using Instella.Core.Trust;

namespace Instella.CLI.Commands;

/// <summary><c>instella keys generate|show</c>: publisher signing keys.</summary>
internal static class KeysCommand
{
    public static Command Create()
    {
        var keys = new Command("keys", "Create and inspect publisher signing keys (ECDSA P-256)");
        keys.Subcommands.Add(CreateGenerate());
        keys.Subcommands.Add(CreateShow());
        return keys;
    }

    private static Command CreateGenerate()
    {
        var outOption = new Option<FileInfo>("--out", "-o") { Description = "Where to write the private key (PKCS#8 PEM)", Required = true };
        var passwordEnvOption = new Option<string?>("--password-env") { Description = "Environment variable holding a password to encrypt the key with" };
        var forceOption = new Option<bool>("--force") { Description = "Overwrite an existing file" };

        var command = new Command("generate", "Generate a new publisher key pair") { outOption, passwordEnvOption, forceOption };
        command.SetAction(parse =>
        {
            var output = parse.GetValue(outOption)!;
            var passwordEnv = parse.GetValue(passwordEnvOption);
            if (output.Exists && !parse.GetValue(forceOption))
            {
                Console.Error.WriteLine($"Error: {output.FullName} already exists. Pass --force to overwrite it.");
                return ExitCodes.Usage;
            }

            string? password = null;
            if (passwordEnv is not null)
            {
                password = Environment.GetEnvironmentVariable(passwordEnv);
                if (string.IsNullOrEmpty(password))
                {
                    Console.Error.WriteLine($"Error: environment variable {passwordEnv} is empty or unset.");
                    return ExitCodes.Usage;
                }
            }

            using var key = ReleaseKeys.Generate();
            output.Directory?.Create();
            // Created owner-only: 0600 on Unix, a protected ACL (you and SYSTEM) on Windows, so no other account can read the key at any point.
            PrivateKeyFile.Write(output.FullName, ReleaseKeys.ExportPrivateKeyPem(key, password));
            if (PrivateKeyFile.GitWorkTreeOf(output.FullName) is { } repo)
                Console.Error.WriteLine($"Warning: this key is inside a git repository ({repo}); keep it out of source control.");

            var publicKey = ReleaseKeys.PublicKeyOf(key);
            Console.WriteLine($"Private key written to {output.FullName}{(password is null ? " (unencrypted)" : " (encrypted)")}.");
            PrintPublicKey(publicKey);
            Console.WriteLine();
            Console.WriteLine("Keep the private key secret (a CI secret store, not the repository).");
            Console.WriteLine("Generate a second, offline backup key now and add it with a second WithPublisherKey(...):");
            Console.WriteLine("if this key is ever lost with no trusted successor, installations can no longer be updated.");
            return ExitCodes.Success;
        });
        return command;
    }

    private static Command CreateShow()
    {
        var keyOption = new Option<FileInfo>("--key") { Description = "Private key file (PKCS#8 PEM)", Required = true };
        var passwordEnvOption = new Option<string?>("--password-env") { Description = "Environment variable holding the key's password" };

        var command = new Command("show", "Print the public key and key id of a private key") { keyOption, passwordEnvOption };
        command.SetAction(parse =>
        {
            var file = parse.GetValue(keyOption)!;
            if (!file.Exists)
            {
                Console.Error.WriteLine($"Error: {file.FullName} not found.");
                return ExitCodes.Usage;
            }
            try
            {
                var password = parse.GetValue(passwordEnvOption) is { } env ? Environment.GetEnvironmentVariable(env) : null;
                using var key = SigningKeyLoader.Load(File.ReadAllText(file.FullName), password);
                PrintPublicKey(ReleaseKeys.PublicKeyOf(key));
                return ExitCodes.Success;
            }
            catch (ArgumentException ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                return ExitCodes.Usage;
            }
        });
        return command;
    }

    private static void PrintPublicKey(PublisherKey key)
    {
        Console.WriteLine($"Key id:     {key.KeyId}");
        Console.WriteLine($"Public key: {key.PublicKey}");
        Console.WriteLine();
        Console.WriteLine("Add it to the installer:");
        Console.WriteLine($"    .WithPublisherKey(\"{key.PublicKey}\")");
    }
}
