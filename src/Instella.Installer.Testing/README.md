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

Works with any test framework. See `llm.md` in the [repository]({{RepositoryUrl}}) for the full harness surface.
