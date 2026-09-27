using System.CommandLine;
using System.Net;
using Instella.Core.Wire;

namespace Instella.CLI.Commands;

/// <summary>Process exit codes. Every command returns non-zero on failure.</summary>
internal static class ExitCodes
{
    public const int Success = 0;

    /// <summary>Bad arguments, missing input, refused operation.</summary>
    public const int Usage = 1;

    /// <summary>The server returned an error or could not be reached.</summary>
    public const int Server = 2;

    /// <summary>The server rejected the API key (401/403).</summary>
    public const int Auth = 3;

    /// <summary>Signing the release failed (the key, the sign command, or its signature); nothing was uploaded.</summary>
    public const int Signing = 4;

    /// <summary>Maps an HTTP failure to <see cref="Auth"/> or <see cref="Server"/>.</summary>
    public static int ForStatus(HttpStatusCode? status) =>
        status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? Auth : Server;
}

/// <summary>Options shared by the server-facing commands.</summary>
internal static class CommonOptions
{
    public static Option<string> Server() => new("--server", "-s")
    {
        Description = "Server URL (https; http only for loopback or with --allow-insecure)",
        Required = true,
    };

    public static Option<string?> ApiKey() => new("--api-key", "-k")
    {
        Description = "API key (default: INSTELLA_API_KEY environment variable)",
    };

    public static Option<bool> AllowInsecure() => new("--allow-insecure")
    {
        Description = "Allow a plain-http server URL on a non-loopback host",
    };

    /// <summary>Applies <see cref="ServerUrlPolicy"/>; writes the reason and returns false when refused.</summary>
    public static bool CheckServerUrl(string url, bool allowInsecure)
    {
        if (ServerUrlPolicy.Check(url, allowInsecure) is { } problem)
        {
            Console.Error.WriteLine($"Error: {problem}.");
            return false;
        }
        if (ServerUrlPolicy.IsInsecure(url))
            Console.Error.WriteLine($"Warning: '{url}' uses plain http; credentials and packages travel unencrypted.");
        return true;
    }

    /// <summary>The explicit key, else <c>INSTELLA_API_KEY</c>, else null.</summary>
    public static string? ResolveApiKey(string? explicitKey) =>
        !string.IsNullOrEmpty(explicitKey) ? explicitKey : Environment.GetEnvironmentVariable("INSTELLA_API_KEY") is { Length: > 0 } env ? env : null;
}
