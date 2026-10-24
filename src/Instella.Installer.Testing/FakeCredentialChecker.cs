using Instella.Core.Platform;

namespace Instella.Installer.Testing;

/// <summary>
/// In-memory accounts for the credential check of the <c>service-account</c> page
/// (<see cref="FakePlatformServices.Credentials"/>, or constructed on its own). An unknown user or a
/// wrong password is a bad user name or password; <see cref="DenyLogonType"/> makes right credentials
/// refused for another reason. User names are case-insensitive. It never clears a password buffer.
/// </summary>
public sealed class FakeCredentialChecker : ICredentialChecker
{
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _users = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _denied = new(StringComparer.OrdinalIgnoreCase);
    private int _checks;

    /// <summary>Adds (or replaces) the account <paramref name="user"/> with <paramref name="password"/>.</summary>
    public void AddUser(string user, string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(user);
        ArgumentNullException.ThrowIfNull(password);
        lock (_gate) _users[user] = password;
    }

    /// <summary>
    /// Right credentials for <paramref name="user"/> are refused as if the logon type were not
    /// granted (<c>ERROR_LOGON_TYPE_NOT_GRANTED</c>), rather than accepted.
    /// </summary>
    public void DenyLogonType(string user)
    {
        ArgumentException.ThrowIfNullOrEmpty(user);
        lock (_gate) _denied.Add(user);
    }

    /// <summary>How many checks were made.</summary>
    public int Checks
    {
        get { lock (_gate) return _checks; }
    }

    Task<CredentialCheckResult> ICredentialChecker.CheckAsync(string user, char[] password, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(password);
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _checks++;
            if (!_users.TryGetValue(user, out var expected) || !password.AsSpan().SequenceEqual(expected))
                return Task.FromResult(new CredentialCheckResult(CredentialCheckOutcome.BadCredentials,
                    "The user name or password is incorrect. (error 1326)"));
            if (_denied.Contains(user))
                return Task.FromResult(new CredentialCheckResult(CredentialCheckOutcome.Refused,
                    "Logon failure: the user has not been granted the requested logon type at this computer. (error 1385)"));
            return Task.FromResult(CredentialCheckResult.Valid);
        }
    }
}
