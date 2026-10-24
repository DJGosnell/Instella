using Instella.Core.Platform.Windows;

namespace Instella.Core.Platform;

/// <summary>
/// Reads, grants and revokes the "Log on as a service" right (<c>SeServiceLogonRight</c>) of an
/// account, through the Local Security Authority. Every call returns a result rather than throwing;
/// only cancellation throws <see cref="OperationCanceledException"/>.
/// </summary>
internal interface IServiceLogonRights
{
    /// <summary>Whether <paramref name="account"/> holds the right itself (not through a group).</summary>
    Task<LogonRightQuery> HasAsync(string account, CancellationToken ct);

    /// <summary>Grants <paramref name="account"/> the right; granting a right it holds is no error.</summary>
    Task<PlatformResult> GrantAsync(string account, CancellationToken ct);

    /// <summary>Revokes the right from <paramref name="account"/>; revoking one it lacks is no error.</summary>
    Task<PlatformResult> RevokeAsync(string account, CancellationToken ct);
}

/// <summary>The outcome of <see cref="IServiceLogonRights.HasAsync"/>.</summary>
/// <param name="Has">True when the account holds the right; false when it does not or the call failed.</param>
/// <param name="Outcome">Success, or failure with a message naming the account.</param>
internal sealed record LogonRightQuery(bool Has, PlatformResult Outcome)
{
    /// <summary>A successful answer.</summary>
    public static LogonRightQuery Of(bool has) => new(has, PlatformResult.Ok);

    /// <summary>A failed query.</summary>
    public static LogonRightQuery Fail(string message) => new(false, PlatformResult.Fail(message));
}

/// <summary>Creates the logon-rights seam for a platform.</summary>
internal static class ServiceLogonRightsFactory
{
    /// <summary>The LSA-backed seam on Windows; otherwise one that fails every call naming the platform.</summary>
    public static IServiceLogonRights Create() =>
        OperatingSystem.IsWindows() ? new WindowsServiceLogonRights() : new UnsupportedServiceLogonRights(PlatformDetector.Current);
}

/// <summary>The logon-rights seam where there is none: every call fails naming the platform, and never throws.</summary>
internal sealed class UnsupportedServiceLogonRights(TargetPlatform platform) : IServiceLogonRights
{
    private string Message => $"service logon rights are not supported on {platform}";

    public Task<LogonRightQuery> HasAsync(string account, CancellationToken ct) => Task.FromResult(LogonRightQuery.Fail(Message));

    public Task<PlatformResult> GrantAsync(string account, CancellationToken ct) => Task.FromResult(PlatformResult.Fail(Message));

    public Task<PlatformResult> RevokeAsync(string account, CancellationToken ct) => Task.FromResult(PlatformResult.Fail(Message));
}
