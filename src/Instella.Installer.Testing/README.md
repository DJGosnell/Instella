# Instella.Installer.Testing

Test your installer's custom steps and pages without touching the machine: an in-memory file
system and registry, fake platform services that record shortcuts, file associations, PATH and
uninstall entries, and a recording log sink.

```csharp
using Instella.Installer.Testing;

await using var harness = InstellaTestHarness.Create()
    .WithAppId("com.example.test")
    .WithInstallPath(@"C:\FakeInstall\Example")
    .WithPageState("welcome", s => s.Set("agree", true))
    .Build();

var result = await harness.RunStepAsync(new MyCustomStep());
Assert.That(result.Success, Is.True);
Assert.That(harness.Registry.Get(RegistryHive.CurrentUser, @"Software\Example", "Name"), Is.EqualTo("test"));
Assert.That(harness.LogSink.Entries.Any(e => e.Message.Contains("configured")), Is.True);
```

Install migrations have their own harness, with every known folder in a fake profile and fake
running programs:

```csharp
var result = await MigrationHarness.For<ReplacePreInstellaCopy>()
    .WithFile(KnownFolder.LocalAppData, "ExampleApp/ExampleApp.exe")
    .WithRunningProcess(KnownFolder.LocalAppData, "ExampleApp/ExampleApp.exe")
    .RunAsync();
Assert.That(result.Outcome, Is.EqualTo(MigrationOutcome.Completed));
Assert.That(result.FileExists(KnownFolder.LocalAppData, "ExampleApp/ExampleApp.exe"), Is.False);
```

Full runs never start real programs: `WhenProgramRuns` decides what the app's upgrade program does
(`ProgramOutcome.Exit(0)`, `Exit(1)`, `TimeOut()`, `CannotStart(…)`), and `ProgramRuns` records how it was started:

```csharp
harness.WhenProgramRuns(run => ProgramOutcome.Exit(1).WithErrorOutput("the database is locked"));
Assert.That(await harness.RunFullWithArgsAsync(["--install", "--silent", "--path", path]), Is.EqualTo(15));
Assert.That(harness.ProgramRuns.Single().Arguments[4], Is.EqualTo("first-install"));
```

Works with any test framework. See `llm.md` in the [repository]({{RepositoryUrl}}) for the full harness surface.
