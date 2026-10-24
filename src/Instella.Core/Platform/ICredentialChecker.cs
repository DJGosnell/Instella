using Instella.Core.Platform.Windows;

namespace Instella.Core.Platform;

/// <summary>
/// Checks a user name and password by trying a logon, without starting anything as that user.
/// Never throws except on cancellation; never keeps, logs or returns the password.
/// </summary>
internal interface ICredentialChecker
{
    /// <summary>
    /// Tries a network logon as <paramref name="user"/>. <paramref name="password"/> is read, not
    /// cleared: the caller owns it.
    /// </summary>
    Task<CredentialCheckResult> CheckAsync(string user, char[] password, CancellationToken ct);
}

/// <summary>What <see cref="ICredentialChecker.CheckAsync"/> found.</summary>
internal enum CredentialCheckOutcome
{
    /// <summary>The user name and password are right.</summary>
    Valid,

    /// <summary>The user name or password is wrong (<c>ERROR_LOGON_FAILURE</c>).</summary>
    BadCredentials,

    /// <summary>The logon was refused for another reason (logon type not granted, account disabled, no domain controller, …).</summary>
    Refused,
}

/// <summary>The outcome of a credential check, with the system's message (empty when valid).</summary>
internal sealed record CredentialCheckResult(CredentialCheckOutcome Outcome, string Message)
{
    /// <summary>The credentials are right.</summary>
    public static CredentialCheckResult Valid { get; } = new(CredentialCheckOutcome.Valid, string.Empty);
}

/// <summary>Creates the credential checker for a platform.</summary>
internal static class CredentialCheckerFactory
{
    /// <summary><c>LogonUserW</c> on Windows; otherwise a checker that refuses naming the platform.</summary>
    public static ICredentialChecker Create() =>
        OperatingSystem.IsWindows() ? new WindowsCredentialChecker() : new UnsupportedCredentialChecker(PlatformDetector.Current);
}

/// <summary>The credential checker where there is none: every check is refused, naming the platform.</summary>
internal sealed class UnsupportedCredentialChecker(TargetPlatform platform) : ICredentialChecker
{
    public Task<CredentialCheckResult> CheckAsync(string user, char[] password, CancellationToken ct) =>
        Task.FromResult(new CredentialCheckResult(CredentialCheckOutcome.Refused, $"credential checks are not supported on {platform}"));
}
