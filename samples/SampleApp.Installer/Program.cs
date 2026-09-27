using System.Reflection;
using Instella.Core.Manifest;
using Instella.Installer.Runtime.Builders;
using Instella.Installer.Runtime.UI.Widgets;

// Read the PE AssemblyVersion (set from <Version>1.2.0</Version> in the
// installer csproj via the SDK defaults) so version lives in exactly one
// place. Bump <Version> in the csproj and both the PE metadata and the
// Instella manifest carry the new value.
var assembly = Assembly.GetExecutingAssembly();
var appVersion = assembly.GetName().Version!;

// The server URL and publisher key are build inputs so the same sample can target a
// local test server (the E2E test publishes it with -p:SampleServerUrl=... and
// -p:SamplePublisherKey=...). Real installers normally hard-code both.
string? Metadata(string key) => assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
    .FirstOrDefault(a => a.Key == key)?.Value is { Length: > 0 } v ? v : null;
var serverUrl = Metadata("SampleServerUrl") ?? "https://updates.example.com";
var publisherKey = Metadata("SamplePublisherKey");

const string LicenceText = """
    MIT License

    Copyright (c) 2026 Instella

    Permission is hereby granted, free of charge, to any person obtaining a copy of this software
    and associated documentation files (the "Software"), to deal in the Software without
    restriction, including without limitation the rights to use, copy, modify, merge, publish,
    distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the
    Software is furnished to do so, subject to the following conditions:

    The above copyright notice and this permission notice shall be included in all copies or
    substantial portions of the Software.

    THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING
    BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
    NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM,
    DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
    OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
    """;

// Forward slashes work on every build host; a backslash is a file-name character on Linux.
var icon = ImageSource.FromFile("../SampleApp/Assets/icon.ico");

var builder = InstellaInstaller.Create()
    .WithApp("QuickNotes", "com.instella.quicknotes", appVersion)
    .WithServer(serverUrl)
    .WithPublisher("Instella Samples")
    .WithHomepage("https://github.com/example/instella")
    .WithLicense("https://opensource.org/licenses/MIT")
    .WithDescription("A simple note-taking application demonstrating Instella packaging")
    .WithIcon(icon)
    .WithExecutableName("QuickNotes")
    .WithShortcuts(s => s.Desktop().StartMenu())
    .WithFileAssociation(".qnote", "QuickNotes Document", icon)
    .WithFileAssociation(".qn", "QuickNotes File")
    .WithAutoStart()
    .WithLaunchAfterInstall()
    .WithElevation(ElevationMode.UserChoice)
    .WithChannel("stable")
    // A licence page. Interactive installs cannot continue until the box is ticked; a silent
    // install needs --accept-license, which sets the same page-state key (license.agree).
    // `QuickNotes.Installer.exe --silent` without it exits 14 (InstallSilentMissingState).
    .AddPage("license", p => p
        .Heading("Licence agreement")
        .ScrollableText(LicenceText)
        .CheckBox("agree", "I accept the terms of the licence")
        .ContinueWhen(s => s.Bool("agree")))
    .AddCliFlag<bool>("accept-license", false, "Accept the licence terms (required with --silent)")
    .MapCliFlag("accept-license", "license.agree");

// Updates are accepted only when signed by the publisher key. Without one (a plain
// local build of the sample) fall back to the explicit development escape hatch.
// With a key the installer also offers a newer published version before it installs, and
// accepts --list-versions / --app-version / --choose-version (another version's own
// installer is downloaded, verified against the key, and run).
if (publisherKey is not null)
    builder.WithPublisherKey(publisherKey).WithNewerVersionPrompt().WithVersionSelection();
else
    builder.AllowUnsignedUpdates();

return await builder.Build().RunAsync(args);
