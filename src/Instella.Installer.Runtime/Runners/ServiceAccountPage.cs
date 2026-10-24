using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.Installation;
using Instella.Core.Logging;
using Instella.Core.Platform;
using Instella.Installer.Runtime.Builders;

namespace Instella.Installer.Runtime.Runners;

/// <summary>
/// The built-in <c>service-account</c> wizard page, shown when a service runs as
/// <see cref="ServiceAccount.FromInstaller"/>: a user name, a password and its confirmation. This
/// class holds when it is shown and the check it runs when the user presses Next.
/// </summary>
internal static class ServiceAccountPage
{
    /// <summary>The page id.</summary>
    public const string Id = "service-account";

    /// <summary>The user-name <c>TextInput</c>'s id.</summary>
    public const string UserId = "user";

    /// <summary>The password <c>PasswordInput</c>'s id.</summary>
    public const string PasswordId = "password";

    /// <summary>The confirmation <c>PasswordInput</c>'s id.</summary>
    public const string ConfirmId = "confirm";

    /// <summary>
    /// Whether the page is shown: never without a <see cref="ServiceAccount.FromInstaller"/> service;
    /// always on a first install; on an upgrade or repair only when one of those services has no
    /// custom account recorded in <paramref name="existing"/> (so the installer must ask for one).
    /// </summary>
    public static bool IsShown(FrozenConfig config, InstallerMode mode, InstalledManifest? existing)
    {
        ArgumentNullException.ThrowIfNull(config);
        var services = config.FromInstallerServices;
        if (services.Count == 0) return false;
        return mode switch
        {
            InstallerMode.FirstInstall => true,
            InstallerMode.Upgrade or InstallerMode.Repair => services.Any(s => RecordedAccount(existing, s.Name) is null),
            _ => false,
        };
    }

    /// <summary>
    /// The custom user account <paramref name="existing"/> records for the service named
    /// <paramref name="serviceName"/>, or null when it records none (no such service, LocalSystem, a
    /// built-in or virtual account, or a gMSA).
    /// </summary>
    public static string? RecordedAccount(InstalledManifest? existing, string serviceName)
    {
        var recorded = existing?.Services?.FirstOrDefault(s => string.Equals(s.Name, serviceName, StringComparison.OrdinalIgnoreCase));
        return ServiceAccount.IsRecordedCustomUser(recorded?.AccountName) ? recorded!.AccountName : null;
    }

    /// <summary>
    /// The user name the page starts with: on an upgrade or repair the account recorded for the
    /// first <see cref="ServiceAccount.FromInstaller"/> service, otherwise that service's default user.
    /// </summary>
    public static string DefaultUser(FrozenConfig config, InstalledManifest? existing)
    {
        var first = config.FromInstallerServices.FirstOrDefault();
        if (first is null) return string.Empty;
        return RecordedAccount(existing, first.Name) ?? first.Account.DefaultUser ?? string.Empty;
    }

    /// <summary>
    /// The check run when the user presses Next: returns why the page may not be left, or null.
    /// No logon is tried while a field is empty or the passwords differ; then one network logon is
    /// tried, and only a wrong user name or password blocks: any other refusal (the logon type is
    /// not granted, for example) is logged and let through, since the service logs on differently.
    /// <paramref name="confirm"/> is zeroed before returning; <paramref name="password"/> is kept for
    /// <c>register-services</c>. The password is never logged.
    /// </summary>
    public static async Task<string?> CheckAsync(
        string user, char[] password, char[] confirm, ICredentialChecker checker, IInstellaLogger log, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(password);
        ArgumentNullException.ThrowIfNull(confirm);
        ArgumentNullException.ThrowIfNull(checker);
        ArgumentNullException.ThrowIfNull(log);
        try
        {
            var account = ServiceAccount.NormalizeUser(user ?? string.Empty);
            if (account.Length == 0) return "Enter the user name of the account the service runs as.";
            if (password.Length == 0) return "Enter the account's password.";
            if (!password.AsSpan().SequenceEqual(confirm)) return "The passwords do not match.";

            var result = await checker.CheckAsync(account, password, ct).ConfigureAwait(false);
            switch (result.Outcome)
            {
                case CredentialCheckOutcome.Valid:
                    log.Info($"service-account: the credentials of '{account}' were checked");
                    return null;
                case CredentialCheckOutcome.BadCredentials:
                    log.Info($"service-account: the user name or password of '{account}' is wrong");
                    return $"The user name or password is wrong: {result.Message}";
                default:
                    log.Warn($"service-account: a test logon as '{account}' was refused ({result.Message}); going on, since the service logs on as a service");
                    return null;
            }
        }
        finally
        {
            Array.Clear(confirm);
        }
    }
}
