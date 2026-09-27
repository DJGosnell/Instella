namespace Instella.Sdk;

/// <summary>
/// The update the app was just restarted after (<see cref="InstellaClient.PostUpdate"/>).
/// Reported once per user: the first start of the app by each user after the update sees it.
/// </summary>
/// <param name="FromVersion">The version before the update: use it for data migrations.</param>
/// <param name="ToVersion">The version after the update.</param>
/// <param name="Channel">The channel of the release that was installed.</param>
/// <param name="CompletedAt">When the update finished.</param>
/// <param name="Arguments"><see cref="UpdateOptions.AdditionalArgs"/> of the update, or empty.</param>
public sealed record PostUpdateInfo(
    Version FromVersion, Version ToVersion, string Channel, DateTimeOffset CompletedAt, IReadOnlyList<string> Arguments);
