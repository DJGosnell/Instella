using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Instella.Core.Installation;
using Instella.Installer.Runtime.Builders;
using Instella.Installer.Runtime.Runners;

namespace Instella.Installer.Runtime.Installation;

/// <summary>
/// The account every <see cref="ServiceAccount.FromInstaller"/> service of one run shares, and its
/// password when the service manager needs one. The password lives in a buffer this object owns:
/// <see cref="CopyPassword"/> hands out copies (the service-manager seam zeroes what it gets), and
/// <see cref="ClearPassword"/> (and <see cref="Dispose"/>) zeroes it. It is never logged or persisted.
/// </summary>
internal sealed class ServiceCredentials : IDisposable
{
    /// <summary>The silent flag naming the account.</summary>
    public const string AccountFlag = "--service-account";

    /// <summary>The silent flag naming a file holding the password.</summary>
    public const string PasswordFileFlag = "--service-password-file";

    private char[]? _password;

    /// <param name="account">The account, normalised (<see cref="ServiceAccount.NormalizeUser"/>).</param>
    /// <param name="password">The password buffer, now owned by this object; null when none is needed.</param>
    public ServiceCredentials(string account, char[]? password)
    {
        ArgumentException.ThrowIfNullOrEmpty(account);
        Account = account;
        _password = password;
    }

    /// <summary>The account name written to the service manager.</summary>
    public string Account { get; }

    /// <summary>Whether a password is held.</summary>
    public bool HasPassword => _password is not null;

    /// <summary>A fresh copy of the password, or null when none is held (the service manager keeps its stored secret).</summary>
    public char[]? CopyPassword() => _password is null ? null : (char[])_password.Clone();

    /// <summary>Zeroes the password and forgets it.</summary>
    public void ClearPassword()
    {
        if (_password is null) return;
        Array.Clear(_password);
        _password = null;
    }

    /// <inheritdoc/>
    public void Dispose() => ClearPassword();

    /// <summary>
    /// The credentials of a silent run, before anything is written: null when no service uses
    /// <see cref="ServiceAccount.FromInstaller"/>. The account is <c>--service-account</c>, else (upgrade,
    /// repair) the account the existing installation records, else the first such service's default user. The
    /// password is needed on a first install, or when the account differs from the one recorded for
    /// any such service; it is read from <c>--service-password-file</c> (one trailing newline
    /// trimmed), else from the configured environment variable (empty counts as absent).
    /// </summary>
    /// <exception cref="SilentMissingStateException">The account, or a needed password, is missing or unreadable (exit 14).</exception>
    public static ServiceCredentials? ResolveSilently(
        FrozenConfig config, InstallerMode mode, IReadOnlyList<string> args, InstalledManifest? existing)
    {
        var services = config.FromInstallerServices;
        if (services.Count == 0) return null;

        var own = OwnArgs(args);
        var flagAccount = ValueOf(own, AccountFlag);
        var passwordFile = ValueOf(own, PasswordFileFlag);

        // Like the wizard: an upgrade or repair keeps the account the installation records over the
        // author's default, so an account chosen at install time survives a plain silent upgrade.
        var recorded = mode is InstallerMode.Upgrade or InstallerMode.Repair
            ? services.Select(s => ServiceAccountPage.RecordedAccount(existing, s.Name)).FirstOrDefault(a => a is not null)
            : null;
        var account = !string.IsNullOrWhiteSpace(flagAccount)
            ? ServiceAccount.NormalizeUser(flagAccount)
            : recorded ?? services[0].Account.DefaultUser;
        if (string.IsNullOrEmpty(account))
            throw new SilentMissingStateException(
                $"service '{services[0].Name}' runs as an account the installer supplies: pass {AccountFlag} <user> "
                + $"with {PasswordFileFlag} <path> (or the {config.ServicePasswordVariableOrDefault} environment variable)");

        var needsPassword = mode == InstallerMode.FirstInstall
            || services.Any(s => !string.Equals(ServiceAccountPage.RecordedAccount(existing, s.Name), account, StringComparison.OrdinalIgnoreCase));

        char[]? password = null;
        if (passwordFile is not null)
            password = ReadPasswordFile(passwordFile);
        else if (needsPassword)
        {
            var value = Environment.GetEnvironmentVariable(config.ServicePasswordVariableOrDefault);
            if (!string.IsNullOrEmpty(value)) password = value.ToCharArray();
        }

        if (needsPassword && (password is null || password.Length == 0))
        {
            if (password is not null) Array.Clear(password);
            throw new SilentMissingStateException(
                $"the service account '{account}' needs its password: pass {PasswordFileFlag} <path> "
                + $"or set the {config.ServicePasswordVariableOrDefault} environment variable");
        }
        return new ServiceCredentials(account, password);
    }

    /// <summary>Reads the whole file once into a buffer, trimming one trailing <c>\r\n</c> or <c>\n</c>; the file is left in place.</summary>
    private static char[] ReadPasswordFile(string path)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new SilentMissingStateException($"{PasswordFileFlag}: '{path}' could not be read: {ex.Message}");
        }
        try
        {
            var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            var start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
            var chars = new char[encoding.GetCharCount(bytes, start, bytes.Length - start)];
            encoding.GetChars(bytes, start, bytes.Length - start, chars, 0);
            var length = chars.Length;
            if (length > 0 && chars[length - 1] == '\n')
            {
                length--;
                if (length > 0 && chars[length - 1] == '\r') length--;
            }
            if (length == chars.Length) return chars;
            var trimmed = chars[..length];
            Array.Clear(chars);
            return trimmed;
        }
        finally
        {
            Array.Clear(bytes);
        }
    }

    /// <summary>The installer's own arguments: everything after <c>--extra-args</c> belongs to the app.</summary>
    private static IReadOnlyList<string> OwnArgs(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
            if (args[i] == "--extra-args") return args.Take(i).ToArray();
        return args;
    }

    private static string? ValueOf(IReadOnlyList<string> args, string name)
    {
        string? value = null;
        for (var i = 0; i < args.Count; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    throw new SilentMissingStateException($"{name} needs a value");
                value = args[++i];
            }
            else if (args[i].StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
                value = args[i][(name.Length + 1)..];
        }
        return value;
    }
}
