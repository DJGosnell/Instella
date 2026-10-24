using Instella.Core.Platform;

namespace Instella.Installer.Testing;

/// <summary>
/// In-memory "Log on as a service" rights (<see cref="FakePlatformServices.LogonRights"/>): the
/// accounts that hold <c>SeServiceLogonRight</c>, without touching the host's security policy.
/// Account names are case-insensitive.
/// </summary>
public sealed class FakeLogonRights : IServiceLogonRights
{
    private readonly object _gate = new();
    private readonly HashSet<string> _holders = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _calls = [];

    /// <summary>Whether <paramref name="account"/> holds the right now.</summary>
    public bool Has(string account)
    {
        ArgumentNullException.ThrowIfNull(account);
        lock (_gate) return _holders.Contains(account);
    }

    /// <summary>
    /// Gives <paramref name="account"/> the right, as an administrator would have before the
    /// install. Not recorded in <see cref="Calls"/>.
    /// </summary>
    public void Grant(string account)
    {
        ArgumentException.ThrowIfNullOrEmpty(account);
        lock (_gate) _holders.Add(account);
    }

    /// <summary>The grants and revokes the installer made, in order: <c>grant:{account}</c> and <c>revoke:{account}</c>.</summary>
    public IReadOnlyList<string> Calls
    {
        get { lock (_gate) return _calls.ToArray(); }
    }

    Task<LogonRightQuery> IServiceLogonRights.HasAsync(string account, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(LogonRightQuery.Of(Has(account)));
    }

    Task<PlatformResult> IServiceLogonRights.GrantAsync(string account, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _calls.Add("grant:" + account);
            _holders.Add(account);
        }
        return Task.FromResult(PlatformResult.Ok);
    }

    Task<PlatformResult> IServiceLogonRights.RevokeAsync(string account, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _calls.Add("revoke:" + account);
            _holders.Remove(account);
        }
        return Task.FromResult(PlatformResult.Ok);
    }
}
