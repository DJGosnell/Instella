using Avalonia;
using Instella.Sdk;

namespace QuickNotes;

public static class Program
{
    /// <summary>The version this run was updated from, or null when it was not started by the updater.</summary>
    public static Version? UpdatedFrom { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        // A crash or power loss in the middle of an update can leave files from two versions.
        // The journal says so; hand the installation to the stub to put the previous version
        // back, and exit (this process's own files may be among those being repaired).
        if (InstellaClient.GetInstallationHealth() == InstallationHealth.InterruptedUpdate)
        {
            var recovered = InstellaClient.StartRecoveryAsync().GetAwaiter().GetResult();
            return recovered == 0 ? 0 : 1;
        }

        // The first start after an update (once per user) is the place for one-off migrations
        // of user data. The updater restarts the app without arguments; anything the app asked
        // for with UpdateOptions.AdditionalArgs is in PostUpdate.Arguments.
        if (InstellaClient.PostUpdate is { } update)
        {
            UpdatedFrom = update.FromVersion;
            MigrateSettings(UpdatedFrom);
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    private static void MigrateSettings(Version? previous)
    {
        // QuickNotes keeps no settings yet. A real app would upgrade its stored data here,
        // for example: if (previous < new Version(1, 2)) RenameLegacyNotesFolder();
        System.Diagnostics.Trace.WriteLine($"QuickNotes updated from {previous?.ToString() ?? "an unknown version"}");
    }
}
