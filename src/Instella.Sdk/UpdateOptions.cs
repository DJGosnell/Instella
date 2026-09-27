namespace Instella.Sdk;

/// <summary>
/// Options for controlling the update process.
/// </summary>
public sealed record UpdateOptions
{
    /// <summary>
    /// Gets or sets whether the updater is allowed to force-close the application
    /// if the user requests it. Default is true.
    /// </summary>
    public bool AllowForceClose { get; init; } = true;

    /// <summary>
    /// Gets or sets the timeout for graceful application close before
    /// force termination is attempted. Default is 30 seconds.
    /// </summary>
    public TimeSpan GracefulCloseTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Arguments for the restarted application. They reach it through
    /// <see cref="InstellaClient.PostUpdateArguments"/>, not its command line: the app is
    /// restarted without elevation through the shell, which cannot pass arguments.
    /// </summary>
    public string[]? AdditionalArgs { get; init; }

    /// <summary>
    /// Gets or sets whether to restart the application after the update completes.
    /// Default is true.
    /// </summary>
    public bool RestartAfterUpdate { get; init; } = true;

    /// <summary>
    /// How long the update window shows "{App} was updated" and counts down before it restarts
    /// the app (the user can press "Restart now"). Default is 5 seconds; <see cref="TimeSpan.Zero"/>
    /// closes the window and restarts at once. Whole seconds; ignored when silent.
    /// </summary>
    public TimeSpan RestartCountdown { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets or sets whether to run the updater in silent mode (no UI).
    /// Default is false.
    /// </summary>
    public bool Silent { get; init; }

    /// <summary>
    /// Gets or sets whether to prefer using a patch update if available.
    /// Default is true. If false, always downloads the full update.
    /// </summary>
    public bool PreferPatch { get; init; } = true;
}
