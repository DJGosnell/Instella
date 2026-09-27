using System;
using System.Collections.Generic;
using System.IO;
using Instella.Core.FileSystem;
using Instella.Core.Installation;
using Instella.Core.Logging;
using Instella.Core.Manifest;
using Instella.Core.Platform;
using Instella.Installer.Runtime.Core;

namespace Instella.Installer.Runtime.Installation;

/// <summary>
/// Per-run state handed to every install step (<c>ctx</c>). Steps read the configuration,
/// the user's answers (<see cref="Cli"/>, <see cref="Pages"/>) and the platform services,
/// and record what they change through the <c>Track*</c> methods so a failed install, and
/// later the uninstaller, can reverse it.
/// </summary>
public sealed class InstallContext
{
    internal InstallContext()
    {
    }

    /// <summary>The application display name.</summary>
    public required string AppName { get; init; }
    /// <summary>The application id.</summary>
    public required string AppId { get; init; }
    /// <summary>The version being installed.</summary>
    public required Version AppVersion { get; init; }
    /// <summary>The resolved install directory.</summary>
    public required string InstallPath { get; init; }
    /// <summary>First install, upgrade, repair, and so on.</summary>
    public required InstallerMode Mode { get; init; }
    /// <summary>Per-user or machine-wide.</summary>
    public required InstallationScope Scope { get; init; }
    /// <summary>The installer configuration, as frozen by the builder.</summary>
    public required InstellaManifest Manifest { get; init; }
    /// <summary>The options chosen for this run (shortcuts, PATH, auto-start, ...).</summary>
    public required InstallOptions Options { get; init; }
    /// <summary>Operating-system services: shortcuts, registry, PATH, file associations.</summary>
    public required IPlatformServices Platform { get; init; }
    /// <summary>The file system; use it rather than <c>System.IO</c> so tests can substitute a fake.</summary>
    public required IFileSystem FileSystem { get; init; }
    /// <summary>The installer log.</summary>
    public required IInstellaLogger Log { get; init; }

    /// <summary>
    /// Parsed values of the flags declared with <c>AddCliFlag</c>, e.g.
    /// <c>ctx.Cli.Get&lt;string&gt;("license-key")</c>.
    /// </summary>
    public Runtime.Builders.CliArgs Cli { get; init; } = Runtime.Builders.CliArgs.Empty;

    /// <summary>
    /// Final state of every page the run showed (or, in silent mode, answered), keyed by
    /// page id, e.g. <c>ctx.Pages["welcome"].Bool("agree")</c>.
    /// </summary>
    public IReadOnlyDictionary<string, UI.Widgets.PageState> Pages { get; init; } =
        new Dictionary<string, UI.Widgets.PageState>(StringComparer.Ordinal);

    /// <summary>
    /// The installation being upgraded or repaired (<see cref="InstallerMode.Upgrade"/> /
    /// <see cref="InstallerMode.Repair"/>); null for a first install. Registration steps use
    /// it to restore, rather than remove, what the previous version had registered when a
    /// later step fails.
    /// </summary>
    public InstalledManifest? ExistingInstallation { get; init; }

    /// <summary>
    /// The transaction every payload file, the stub and the installed manifest are staged
    /// into. Created by the extract step; committed by the built-in <c>commit-transaction</c>
    /// step at the start of <see cref="InstallStage.Finalize"/>. Until then the new files are
    /// staged, not live: steps in earlier stages must not expect them under
    /// <see cref="InstallPath"/> (use <see cref="ExtractedFiles"/> to see what is being installed).
    /// </summary>
    internal Core.Transactions.InstallTransaction? Transaction { get; set; }

    /// <summary>
    /// Offline installer archive (ZIP) positioned at entry 0. Not required for
    /// server-download modes — runs without an embedded payload pass
    /// <c>null</c>.
    /// </summary>
    internal Stream? PayloadArchive { get; set; }

    /// <summary>
    /// The application files being installed, with their hashes and sizes. Empty until the
    /// <see cref="InstallStage.Extract"/> stage has run.
    /// </summary>
    public IReadOnlyList<InstalledFile> ExtractedFiles { get; internal set; } = Array.Empty<InstalledFile>();

    /// <summary>Full path of the application's main executable, once the registration steps have resolved it.</summary>
    public string? ExecutablePath { get; internal set; }

    /// <summary>Whether CreateShortcutsStep actually created the desktop shortcut.</summary>
    internal bool CreatedDesktopShortcut { get; set; }

    /// <summary>Whether CreateShortcutsStep actually created the start-menu shortcut.</summary>
    internal bool CreatedStartMenuShortcut { get; set; }

    /// <summary>Whether AddToPathStep actually added the install directory to PATH.</summary>
    internal bool AddedToPath { get; set; }

    /// <summary>Whether ConfigureAutoStartStep actually configured launch-on-login.</summary>
    internal bool ConfiguredAutoStart { get; set; }

    /// <summary>Extensions RegisterFileAssociationsStep actually registered.</summary>
    internal List<string> RegisteredAssociations { get; } = new();

    /// <summary>Integrations of the previous version this run removed (<c>remove-dropped-integrations</c>).</summary>
    internal List<string> DroppedIntegrations { get; } = new();

    /// <summary>Whether RegisterUninstallEntryStep successfully created the ARP / Installed Apps entry.</summary>
    internal bool UninstallEntryRegistered { get; set; }

    /// <summary>
    /// Whether steps may show a UAC prompt (a prerequisite that needs elevation). True only for
    /// the interactive wizard; silent installs never prompt.
    /// </summary>
    internal bool AllowElevationPrompt { get; init; }

    /// <summary>Set when a step (e.g. a prerequisite returning 3010) needs a reboot; the finish page shows it.</summary>
    public bool RebootRequired { get; set; }

    /// <summary>The <c>WithAppManagedAutoStart</c> Run value names, recorded as adopted items in the manifest.</summary>
    internal IReadOnlyList<string> AppManagedRunValues { get; init; } = [];

    /// <summary>Install migrations: the seams they reach the machine through, and what they did in this run.</summary>
    internal Migrations.MigrationRuntime Migrations { get; set; } = new();

    /// <summary>Best-effort clean-ups the executor runs once every step has succeeded (migration undo backups).</summary>
    internal List<Func<System.Threading.Tasks.Task>> CompletionActions { get; } = new();

    /// <summary>Ledger for tracked mutations. Covers files, directories, registry values/keys, and PATH entries.</summary>
    internal TrackingLedger Ledger { get; } = new();

    /// <summary>
    /// Registry writes recorded for the manifest. <c>WriteRegistrySpecsStep</c>
    /// appends one <see cref="ManifestRegistryEntry"/> per successful write;
    /// <c>WriteManifestStep</c> serializes them into the v3 manifest's
    /// <c>registry</c> section so the uninstaller can reverse each entry.
    /// </summary>
    internal List<ManifestRegistryEntry> ManifestRegistryEntries { get; } = new();

    /// <summary>User-declared CLI flags for the v3 manifest's <c>cli.declaredFlags</c> section. Populated by the mode runner before step execution.</summary>
    internal IReadOnlyList<ManifestCliFlag>? ManifestCliFlags { get; set; }

    /// <summary>Logging config snapshot for the v3 manifest's <c>logging</c> section. Populated by the mode runner before step execution.</summary>
    internal ManifestLoggingConfig? ManifestLogging { get; set; }

    /// <summary>Record that the current step created a file, so a failed install (and the uninstaller) deletes it.</summary>
    public void TrackFile(string path) => Ledger.TrackFile(path);

    /// <summary>Record that the current step created a directory, so a failed install (and the uninstaller) deletes it; <paramref name="recursive"/> also deletes its contents.</summary>
    public void TrackDirectory(string path, bool recursive = false) => Ledger.TrackDirectory(path, recursive);

    /// <summary>
    /// Record that the current step has written a Windows registry value, so
    /// the tracked-rollback unwind can delete it on later install failure.
    /// Linux/macOS platform services no-op the actual deletion.
    /// </summary>
    public void TrackRegistryValue(RegistryHive hive, string keyPath, string name)
        => Ledger.TrackRegistryValue(hive, keyPath, name);

    /// <summary>
    /// Record that the current step is overwriting an existing registry value, so the
    /// tracked-rollback unwind writes <paramref name="previous"/> back.
    /// </summary>
    public void TrackRegistryValueRestore(RegistryHive hive, string keyPath, string name, RegistryValueData previous)
        => Ledger.TrackRegistryValueRestore(hive, keyPath, name, previous);

    /// <summary>
    /// Record that the current step has created a Windows registry key, so the
    /// tracked-rollback unwind can delete it (recursively) on later install
    /// failure.
    /// </summary>
    public void TrackRegistryKey(RegistryHive hive, string keyPath)
        => Ledger.TrackRegistryKey(hive, keyPath);

    /// <summary>
    /// Record that the current step has added a directory to PATH, so the
    /// tracked-rollback unwind can remove it on later install failure.
    /// </summary>
    public void TrackPathEntry(string directory)
        => Ledger.TrackPathEntry(directory);
}
