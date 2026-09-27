# Instella.Installer.Runtime

The fluent installer builder, the Win32 wizard and the install, update, repair and uninstall
pipeline. Reference it from your `*.Installer` project together with `Instella.Installer.Build`;
your compiled installer project *is* the installer.

```csharp
using Instella.Core.Manifest;                 // ElevationMode
using Instella.Installer.Runtime.Builders;
using Instella.Installer.Runtime.UI.Widgets;  // ImageSource

return await InstellaInstaller.Create()
    .WithApp("QuickNotes", "com.example.quicknotes", new Version(1, 2, 0))
    .WithServer("https://updates.example.com")
    .WithPublisherKey("MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE...")   // from `instella keys generate`
    .WithPublisher("Example Corp")
    .WithIcon(ImageSource.FromFile("../QuickNotes/Assets/icon.ico"))
    .WithShortcuts(s => s.Desktop().StartMenu())
    .WithElevation(ElevationMode.UserChoice)
    .AddPage("license", p => p
        .ScrollableText("...licence text...")
        .CheckBox("agree", "I accept the licence")
        .ContinueWhen(s => s.Bool("agree")))
    .AddCliFlag<bool>("accept-license")
    .MapCliFlag("accept-license", "license.agree")   // --silent needs --accept-license, else exit 14
    .Build()
    .RunAsync(args);
```

Updates are accepted only when their release manifest is signed by a key given to
`WithPublisherKey`. Windows is supported; Linux and macOS are experimental (silent installs only).

Documentation: the [repository]({{RepositoryUrl}}) README, `llm.md` there (full builder reference) and the [docs]({{DocsUrl}}).
