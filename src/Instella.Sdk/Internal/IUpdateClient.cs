using Instella.Core.Update;

namespace Instella.Sdk.Internal;

/// <summary>
/// Server communication the SDK needs. Downloads are the updater's job, not the app's,
/// so the SDK only asks whether an update exists.
/// </summary>
internal interface IUpdateClient
{
    /// <summary>Checks <paramref name="channel"/> for a verified update of <paramref name="info"/>.</summary>
    Task<UpdateCheckResult> CheckAsync(InstellaInfo info, string channel, CancellationToken ct);
}
