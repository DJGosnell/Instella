namespace Instella.Installer.Runtime.Builders;

/// <summary>
/// Static entry point to the fluent installer builder. The user's
/// <c>*.Installer</c> project's <c>Program.Main</c> is expected to do:
/// <code>
/// return await InstellaInstaller.Create()
///     .WithApp("MyApp", "com.example.myapp", new Version(1, 0, 0))
///     .WithServer("https://updates.example.com")
///     .AddStep("my-step", sb =&gt; sb
///         .InStage(InstallStage.Register)
///         .Execute(async (ctx, progress, ct) =&gt; StepResult.Ok)
///         .NoRollbackNeeded("idempotent"))
///     .Build()
///     .RunAsync(args);
/// </code>
/// </summary>
/// <remarks>
/// The same configuration code also runs at build time, in a build of the project for the
/// build machine, to emit the installer's manifest. It must therefore be free of side effects
/// and must not branch on the build machine's operating system or environment: use
/// <see cref="InstallerBuilder.OnWindows"/> and friends, which apply at install time.
/// </remarks>
public static class InstellaInstaller
{
    /// <summary>Create a new builder. Each call yields an independent instance.</summary>
    public static InstallerBuilder Create() => new();
}
