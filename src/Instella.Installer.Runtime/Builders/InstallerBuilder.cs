using System;
using System.Collections.Generic;
using Instella.Core.Installation;
using Instella.Core.Logging;
using Instella.Core.Manifest;
using Instella.Core.Trust;
using Instella.Core.Wire;
using Instella.Installer.Runtime.Installation;
using Instella.Installer.Runtime.Installation.Builders;
using Instella.Installer.Runtime.UI.Widgets;

namespace Instella.Installer.Runtime.Builders;

/// <summary>
/// Fluent configuration surface for an Instella installer. Obtain one from
/// <see cref="InstellaInstaller.Create"/>, chain the metadata and side-effect methods you
/// need, and call <see cref="Build"/> to freeze the configuration into an
/// <see cref="IInstellaInstaller"/>. Missing or conflicting configuration is reported by
/// <see cref="Build"/> as an <see cref="InvalidOperationException"/>.
/// </summary>
/// <remarks>
/// A sealed class rather than an interface: new configuration methods can be
/// added in minor versions without breaking anyone. Every method returns this builder.
/// </remarks>
public sealed class InstallerBuilder
{
    private string? _appName;
    private string? _appId;
    private Version? _appVersion;
    private string _serverUrl = string.Empty;
    private string _channel = "stable";
    private string? _publisher;
    private string? _homepageUrl;
    private string? _licenseUrl;
    private string? _description;
    private ImageSource? _icon;
    private ImageSource? _brandImage = UI.InstellaBranding.Logo;
    private Func<InstallContext, string>? _installPathResolver;
    private ElevationMode _elevation = ElevationMode.UserChoice;
    private string? _executableName;
    private ShortcutConfig? _shortcuts;
    private readonly List<FileAssociation> _fileAssociations = new();
    private bool _autoStart;
    private bool? _offerLaunchAfterInstall;
    private bool _offerNewerVersion;
    private bool _allowVersionSelection;
    private bool _pathRegistration;
    private readonly List<Prerequisite> _prerequisites = new();
    private readonly List<StepBuilder> _userSteps = new();
    private readonly List<PageBuilder> _userPages = new();
    private readonly List<RegistryWriteSpec> _registryWrites = new();
    private readonly List<CliFlagSpec> _declaredCliFlags = new();
    private readonly Dictionary<string, string> _cliFlagMappings = new(StringComparer.OrdinalIgnoreCase);
    private LoggingBuilder? _loggingBuilder;
    private readonly List<Migrations.InstallMigration> _migrations = new();
    private readonly List<string> _appManagedRunValues = new();
    private bool _previewEnabled;
    private PayloadFilterBuilder? _payloadFilter;
    private readonly List<PublisherKey> _publisherKeys = new();
    private bool _allowUnsignedUpdates;
    private bool _allowInsecureServer;
    private string? _downloadToken;

    internal InstallerBuilder()
    {
    }

    /// <summary>
    /// Required. The application's display name, unique id (for example <c>com.example.myapp</c>) and the version this
    /// installer installs. A zero revision is dropped (an assembly version <c>1.2.0.0</c> becomes <c>1.2.0</c>), as
    /// <c>instella upload</c> does, so the installed version matches the release uploaded for it.
    /// </summary>
    public InstallerBuilder WithApp(string name, string appId, Version version)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(appId);
        ArgumentNullException.ThrowIfNull(version);
        _appName = name;
        _appId = appId;
        _appVersion = Instella.Core.Utilities.AppVersions.Normalize(version);
        return this;
    }

    /// <summary>
    /// The Instella server that serves updates. Must be https unless the host is loopback
    /// or <see cref="AllowInsecureServer"/> is called; requires <see cref="WithPublisherKey"/>
    /// unless <see cref="AllowUnsignedUpdates"/> is called.
    /// </summary>
    public InstallerBuilder WithServer(string url)
    {
        ArgumentException.ThrowIfNullOrEmpty(url);
        _serverUrl = url;
        return this;
    }

    /// <summary>
    /// The release channel installations follow for updates. Defaults to <c>stable</c>. The
    /// name is lowercased; it must follow <see cref="Instella.Core.Wire.ChannelNames.Rule"/>.
    /// </summary>
    public InstallerBuilder WithChannel(string channel)
    {
        ArgumentException.ThrowIfNullOrEmpty(channel);
        if (!Instella.Core.Wire.ChannelNames.TryNormalize(channel, out var normalized))
            throw new ArgumentException($"'{channel}': {Instella.Core.Wire.ChannelNames.Rule}", nameof(channel));
        _channel = normalized;
        return this;
    }

    /// <summary>Publisher name shown in the wizard and the Installed Apps entry.</summary>
    public InstallerBuilder WithPublisher(string publisher)
    {
        ArgumentException.ThrowIfNullOrEmpty(publisher);
        _publisher = publisher;
        return this;
    }

    /// <summary>Homepage URL shown in the Installed Apps entry.</summary>
    public InstallerBuilder WithHomepage(string url)
    {
        ArgumentException.ThrowIfNullOrEmpty(url);
        _homepageUrl = url;
        return this;
    }

    /// <summary>Licence URL recorded for the application.</summary>
    public InstallerBuilder WithLicense(string url)
    {
        ArgumentException.ThrowIfNullOrEmpty(url);
        _licenseUrl = url;
        return this;
    }

    /// <summary>Short description shown in the wizard.</summary>
    public InstallerBuilder WithDescription(string text)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        _description = text;
        return this;
    }

    /// <summary>
    /// Image asset used as the installer's application icon. Sourced via
    /// <see cref="ImageSource.FromFile"/>, <see cref="ImageSource.FromResource"/>,
    /// or <see cref="ImageSource.FromBytes"/>. Renderers materialize the
    /// bytes on demand; non-file sources are hashed and staged to disk when
    /// a file path is needed (e.g. the Installed Apps entry's
    /// <c>DisplayIcon</c>).
    /// </summary>
    public InstallerBuilder WithIcon(ImageSource icon)
    {
        ArgumentNullException.ThrowIfNull(icon);
        _icon = icon;
        return this;
    }

    /// <summary>
    /// Image shown at the top of the wizard's Welcome page, scaled down to 64 pixels high (at
    /// 100 % display scaling). Without this call the Instella logo is shown; pass your own logo,
    /// or <see langword="null"/> for no image. Use a PNG with transparency at least 128 pixels
    /// high so it stays sharp on high-DPI displays.
    /// </summary>
    public InstallerBuilder WithBrandImage(ImageSource? image)
    {
        _brandImage = image;
        return this;
    }

    /// <summary>
    /// Resolver that returns the final install path at runtime. Receives the
    /// partially-populated <see cref="InstallContext"/> so the resolver can
    /// pick a per-user vs. system-wide path based on <c>ctx.Scope</c>. When
    /// not called, the platform's default install path is used
    /// (<see cref="Instella.Core.Platform.IPlatformServices.GetDefaultInstallPath"/>).
    /// </summary>
    public InstallerBuilder WithInstallPath(Func<InstallContext, string> resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        _installPathResolver = resolver;
        return this;
    }

    /// <summary>Whether the installer installs per user, machine-wide, or lets the user choose. Defaults to <see cref="ElevationMode.UserChoice"/>.</summary>
    public InstallerBuilder WithElevation(ElevationMode mode)
    {
        _elevation = mode;
        return this;
    }

    /// <summary>File name of the application's main executable, relative to the install directory.</summary>
    public InstallerBuilder WithExecutableName(string exeName)
    {
        ArgumentException.ThrowIfNullOrEmpty(exeName);
        _executableName = exeName;
        return this;
    }

    /// <summary>Which shortcuts to create (desktop, Start menu).</summary>
    public InstallerBuilder WithShortcuts(Action<ShortcutBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var sb = new ShortcutBuilder();
        configure(sb);
        _shortcuts = sb.Build();
        return this;
    }

    /// <summary>
    /// Register a file-type association. <paramref name="icon"/> must be a
    /// <see cref="ImageSource.FromFile"/> source (file-association icons are
    /// registered by path in the OS registries); passing a resource- or
    /// bytes-source throws. Use <c>null</c> to defer to the app icon.
    /// </summary>
    public InstallerBuilder WithFileAssociation(string extension, string description, ImageSource? icon = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(extension);
        ArgumentException.ThrowIfNullOrEmpty(description);

        string? iconPath = null;
        if (icon is FileImageSource file)
        {
            iconPath = file.Path;
        }
        else if (icon is not null)
        {
            throw new InvalidOperationException(
                $"WithFileAssociation('{extension}'): file-association icons must be sourced via ImageSource.FromFile — OS registries store them by path.");
        }

        _fileAssociations.Add(new FileAssociation(extension, description, iconPath));
        return this;
    }

    /// <summary>
    /// Offer to start the application when the interactive install finishes: the last page gets a
    /// "Launch {app} when I click Finish" check box, ticked unless <paramref name="checkedByDefault"/>
    /// is false. An elevated (machine-wide) install starts the app as the signed-in user, not as
    /// administrator. Silent installs never start it.
    /// </summary>
    public InstallerBuilder WithLaunchAfterInstall(bool checkedByDefault = true)
    {
        _offerLaunchAfterInstall = checkedByDefault;
        return this;
    }

    /// <summary>
    /// Before an interactive install starts, ask the server whether a newer version has been
    /// published with an online installer (<c>instella upload --installer</c>) and offer it:
    /// "Install {newer} (recommended) / Install {this version} / Cancel". Choosing the newer
    /// version downloads that version's online installer, checks it against the release signed
    /// by a key from <see cref="WithPublisherKey"/>, and runs it, because every version's own
    /// installer carries its own install process. Offline installers offer it too when the
    /// server can be reached; silent installs and <c>--no-newer-check</c> never ask. Needs
    /// <see cref="WithServer"/> and at least one publisher key.
    /// </summary>
    public InstallerBuilder WithNewerVersionPrompt(bool enabled = true)
    {
        _offerNewerVersion = enabled;
        return this;
    }

    /// <summary>
    /// Let users pick the version to install from the command line: <c>--list-versions</c>
    /// prints the versions published with an online installer for their platform (with
    /// changelogs; deprecated ones hidden; <c>--channel</c> picks the channel),
    /// <c>--app-version &lt;version|latest&gt;</c> installs that version (also with
    /// <c>--silent</c>), and <c>--choose-version</c> shows a picker. A version other than this
    /// installer's own is installed by running that version's installer after verifying it
    /// against the release signed by a key from <see cref="WithPublisherKey"/>. Without this
    /// call the flags are refused and the installer always installs its own version. Needs
    /// <see cref="WithServer"/> and at least one publisher key.
    /// </summary>
    public InstallerBuilder WithVersionSelection(bool enabled = true)
    {
        _allowVersionSelection = enabled;
        return this;
    }

    /// <summary>Start the application when the user logs in.</summary>
    public InstallerBuilder WithAutoStart(bool enabled = true)
    {
        _autoStart = enabled;
        return this;
    }

    /// <summary>Add the install directory to the user's (or machine's) PATH.</summary>
    public InstallerBuilder WithPathRegistration(bool enabled = true)
    {
        _pathRegistration = enabled;
        return this;
    }

    /// <summary>Declare a prerequisite that is detected, and installed if missing, before the application.</summary>
    public InstallerBuilder WithPrerequisite(Action<PrerequisiteBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var pb = new PrerequisiteBuilder();
        configure(pb);
        _prerequisites.Add(pb.Build());
        return this;
    }

    /// <summary>Add a custom install step. <paramref name="name"/> must be unique; <paramref name="configure"/> sets its stage, body and rollback.</summary>
    public InstallerBuilder AddStep(string name, Action<StepBuilder> configure)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(configure);
        var sb = StepBuilder.Create(name);
        configure(sb);
        _userSteps.Add(sb);
        return this;
    }

    /// <summary>
    /// Register an install migration: code that runs under a condition during an install, upgrade,
    /// repair or uninstall, for example to replace a copy of the app installed without Instella.
    /// The class is created here, once. Ids must be unique; <see cref="Build"/> validates them.
    /// </summary>
    public InstallerBuilder AddMigration<T>() where T : Migrations.InstallMigration, new() => AddMigration(new T());

    /// <summary>Register an install migration instance (for migrations that take constructor arguments).</summary>
    public InstallerBuilder AddMigration(Migrations.InstallMigration migration)
    {
        ArgumentNullException.ThrowIfNull(migration);
        _migrations.Add(migration);
        return this;
    }

    /// <summary>
    /// The app writes its own "start with Windows" Run value named <paramref name="runValueName"/>
    /// (HKCU for per-user installs, HKLM for machine-wide ones). Uninstall deletes it, but only
    /// while it points into the installation being removed. May be called more than once.
    /// </summary>
    public InstallerBuilder WithAppManagedAutoStart(string runValueName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runValueName);
        if (runValueName.Contains('\\'))
            throw new ArgumentException($"'{runValueName}' is not a registry value name", nameof(runValueName));
        if (!_appManagedRunValues.Contains(runValueName, StringComparer.OrdinalIgnoreCase))
            _appManagedRunValues.Add(runValueName);
        return this;
    }

    /// <summary>
    /// Define a custom wizard page. <paramref name="id"/> must be unique
    /// within the installer. <paramref name="configure"/> receives a
    /// <see cref="PageBuilder"/> that the user populates with widgets and
    /// lifecycle hooks.
    /// </summary>
    public InstallerBuilder AddPage(string id, Action<PageBuilder> configure)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(configure);
        var pb = new PageBuilder(id);
        configure(pb);
        _userPages.Add(pb);
        return this;
    }

    /// <summary>Windows-only configuration. <paramref name="configure"/> runs only when the installer runs on Windows.</summary>
    public InstallerBuilder OnWindows(Action<WindowsBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        if (OperatingSystem.IsWindows())
        {
            var win = new WindowsBuilder();
            configure(win);
            _registryWrites.AddRange(win.Writes);
        }
        return this;
    }

    /// <summary>Linux-only configuration. <paramref name="configure"/> runs only when the installer runs on Linux.</summary>
    public InstallerBuilder OnLinux(Action<LinuxBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        if (OperatingSystem.IsLinux())
        {
            configure(new LinuxBuilder());
        }
        return this;
    }

    /// <summary>macOS-only configuration. <paramref name="configure"/> runs only when the installer runs on macOS.</summary>
    public InstallerBuilder OnMacOS(Action<MacOSBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        if (OperatingSystem.IsMacOS())
        {
            configure(new MacOSBuilder());
        }
        return this;
    }

    /// <summary>
    /// Declare a typed CLI flag. Supported <typeparamref name="T"/>: <see cref="bool"/>,
    /// <see cref="int"/>, <see cref="long"/>, <see cref="string"/>,
    /// <see cref="string"/>[] (comma-separated on the wire),
    /// <see cref="System.IO.FileInfo"/>, <see cref="System.IO.DirectoryInfo"/>.
    /// Other types throw. Names that collide with reserved flags throw at <see cref="Build"/>.
    /// </summary>
    public InstallerBuilder AddCliFlag<T>(string name, T defaultValue = default!, string? help = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (!SupportedCliFlagTypes.IsSupported(typeof(T)))
            throw new InvalidOperationException(
                $"AddCliFlag<{typeof(T).Name}>('{name}'): unsupported type. Supported: bool, int, long, string, string[], FileInfo, DirectoryInfo.");
        _declaredCliFlags.Add(new CliFlagSpec(name, typeof(T), defaultValue, help, MapsTo: null));
        return this;
    }

    /// <summary>
    /// Bind a declared CLI flag to a page-state key (<c>pageId.widgetId</c>). A provided
    /// flag's value is written to that key before the page is shown: it pre-fills the field in
    /// the wizard and answers it under <c>--silent</c>, where a page whose continue condition
    /// still fails exits 14 naming the flag. The key is validated at <see cref="Build"/>.
    /// </summary>
    public InstallerBuilder MapCliFlag(string flagName, string pageStateKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(flagName);
        ArgumentException.ThrowIfNullOrEmpty(pageStateKey);
        _cliFlagMappings[flagName] = pageStateKey;
        return this;
    }

    /// <summary>Configure the installer's log file and level.</summary>
    public InstallerBuilder ConfigureLogging(Action<LoggingBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _loggingBuilder ??= new LoggingBuilder();
        configure(_loggingBuilder);
        return this;
    }

    /// <summary>Shorthand for setting the log level through <see cref="ConfigureLogging"/>.</summary>
    public InstallerBuilder WithLogLevel(InstellaLogLevel level)
    {
        _loggingBuilder ??= new LoggingBuilder();
        _loggingBuilder.WithLevel(level);
        return this;
    }

    /// <summary>
    /// Opt this installer into preview mode: when invoked with <c>--preview</c>, it walks
    /// through the wizard (or prints the headless report) without touching the host system.
    /// Off by default so production installers do not ship a discoverable preview path.
    /// Mode, speed and failure injection come from <c>--preview-mode</c>,
    /// <c>--preview-speed</c> and <c>--preview-fail</c> at run time.
    /// </summary>
    public InstallerBuilder EnablePreview()
    {
        _previewEnabled = true;
        return this;
    }

    /// <summary>
    /// Compose a glob filter that the <c>AppendPayloadToSelf</c> MSBuild task applies to the
    /// payload file set (by zip-entry path) before zipping it into the installer, for example
    /// to drop <c>*.pdb</c> files. Multiple calls accumulate on the same filter.
    /// </summary>
    public InstallerBuilder WithPayloadFilter(Action<PayloadFilterBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _payloadFilter ??= new PayloadFilterBuilder();
        configure(_payloadFilter);
        return this;
    }

    /// <summary>
    /// Trusts updates signed by this publisher key (base64 SubjectPublicKeyInfo of an ECDSA
    /// P-256 key, as printed by <c>instella keys generate</c>). May be called more than once,
    /// for example to add a backup key. When <see cref="WithServer"/> is set, at least one key
    /// is required unless <see cref="AllowUnsignedUpdates"/> is called.
    /// </summary>
    public InstallerBuilder WithPublisherKey(string publicKeyBase64)
    {
        ArgumentException.ThrowIfNullOrEmpty(publicKeyBase64);
        var key = KeyIds.FromPublicKey(publicKeyBase64);
        if (!_publisherKeys.Exists(k => k.KeyId == key.KeyId))
            _publisherKeys.Add(key);
        return this;
    }

    /// <summary>
    /// Development escape hatch: accept updates without publisher signatures. Anyone who
    /// controls the server (or the network path to it) can then push code to installations.
    /// </summary>
    public InstallerBuilder AllowUnsignedUpdates()
    {
        _allowUnsignedUpdates = true;
        return this;
    }

    /// <summary>
    /// Sends this download token to the update server so a package that requires a key
    /// (PackageKeyRequired) can be installed and updated. Anyone who has the installer can read it:
    /// it limits access, it is not a secret. Create tokens in the server's package panel. The
    /// installed app keeps it and uses it for every update.
    /// </summary>
    /// <param name="token">A download token, <c>idt_</c> followed by 43 characters.</param>
    /// <exception cref="ArgumentException"><paramref name="token"/> is not a download token.</exception>
    public InstallerBuilder WithDownloadToken(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        if (!Instella.Core.Wire.DownloadTokens.LooksLikeToken(token.Trim()))
            throw new ArgumentException("not a download token (idt_…); create one in the server's package panel", nameof(token));
        _downloadToken = token.Trim();
        return this;
    }

    /// <summary>Allows a plain-http server URL on a non-loopback host. Logged as a warning on every run.</summary>
    public InstallerBuilder AllowInsecureServer()
    {
        _allowInsecureServer = true;
        return this;
    }

    /// <summary>Freeze the builder into an <see cref="IInstellaInstaller"/>.</summary>
    /// <exception cref="InvalidOperationException">Required fields are missing
    /// (<see cref="WithApp"/>), step names or page ids collide, a server has no publisher key,
    /// or a reserved CLI flag name is redeclared via <see cref="AddCliFlag{T}"/>.</exception>
    public IInstellaInstaller Build()
    {
        if (string.IsNullOrEmpty(_appName) || string.IsNullOrEmpty(_appId) || _appVersion is null)
            throw new InvalidOperationException("InstallerBuilder.Build(): WithApp(name, appId, version) is required.");

        if (!string.IsNullOrEmpty(_serverUrl))
        {
            if (ServerUrlPolicy.Check(_serverUrl, _allowInsecureServer) is { } urlProblem)
                throw new InvalidOperationException($"InstallerBuilder.Build(): {urlProblem}.");

            // A missing key is a build error, never a silent downgrade to "no verification".
            if (_publisherKeys.Count == 0 && !_allowUnsignedUpdates)
                throw new InvalidOperationException(
                    "InstallerBuilder.Build(): WithServer(...) requires WithPublisherKey(...) so updates can be verified. " +
                    "Generate one with 'instella keys generate', or call AllowUnsignedUpdates() for development only.");
        }

        if (_downloadToken is not null && string.IsNullOrEmpty(_serverUrl))
            throw new InvalidOperationException("InstallerBuilder.Build(): WithDownloadToken(...) needs WithServer(...).");

        foreach (var (enabled, name) in new[] { (_offerNewerVersion, "WithNewerVersionPrompt()"), (_allowVersionSelection, "WithVersionSelection()") })
        {
            if (enabled && (string.IsNullOrEmpty(_serverUrl) || _publisherKeys.Count == 0))
                throw new InvalidOperationException(
                    $"InstallerBuilder.Build(): {name} needs WithServer(...) and WithPublisherKey(...): " +
                    "another version's installer is only run after its signed release verifies.");
        }

        var specs = new List<StepSpec>(_userSteps.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sb in _userSteps)
        {
            var spec = sb.Build();
            if (!seen.Add(spec.Name))
                throw new InvalidOperationException($"InstallerBuilder.Build(): duplicate step name '{spec.Name}'.");
            if (spec.Name.StartsWith(Migrations.MigrationValidation.StepPrefix, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"InstallerBuilder.Build(): step name '{spec.Name}' starts with '{Migrations.MigrationValidation.StepPrefix}', which is reserved for migrations.");
            specs.Add(spec);
        }

        var migrations = Migrations.MigrationValidation.ValidateAndSort(_migrations);
        if (_autoStart && _appManagedRunValues.Contains(_appId!, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"InstallerBuilder.Build(): WithAppManagedAutoStart(\"{_appId}\") names the Run value WithAutoStart() writes; use one or the other.");

        var pageSpecs = new List<PageSpec>(_userPages.Count);
        var pageIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pb in _userPages)
        {
            var spec = pb.Build();
            if (!pageIds.Add(spec.Id))
                throw new InvalidOperationException($"InstallerBuilder.Build(): duplicate page id '{spec.Id}'.");
            pageSpecs.Add(spec);
        }

        // Apply MapsTo to the flag specs (MapCliFlag comes after AddCliFlag), resolved to a
        // fully qualified "pageId.widgetId" and type-checked against the widget.
        foreach (var mapped in _cliFlagMappings.Keys)
        {
            if (!_declaredCliFlags.Exists(f => SameFlag(f.Name, mapped)))
                throw new InvalidOperationException(
                    $"InstallerBuilder.Build(): MapCliFlag(\"{mapped}\", ...) maps a flag that was not declared with AddCliFlag.");
        }

        var flagsWithMaps = new List<CliFlagSpec>(_declaredCliFlags.Count);
        foreach (var flag in _declaredCliFlags)
        {
            var normalized = flag.Name.StartsWith("--", StringComparison.Ordinal) ? flag.Name : "--" + flag.Name;
            if (ReservedCliFlags.All.Contains(normalized))
                throw new InvalidOperationException($"InstallerBuilder.Build(): CLI flag '{normalized}' is reserved.");
            var mapping = _cliFlagMappings.FirstOrDefault(kv => SameFlag(kv.Key, flag.Name)).Value;
            var mapsTo = mapping is null ? null : ResolvePageStateKey(flag, mapping, pageSpecs);
            flagsWithMaps.Add(flag with { MapsTo = mapsTo });
        }

        var config = new FrozenConfig(
            AppName: _appName!,
            AppId: _appId!,
            AppVersion: _appVersion!,
            ServerUrl: _serverUrl,
            Channel: _channel,
            Publisher: _publisher,
            HomepageUrl: _homepageUrl,
            LicenseUrl: _licenseUrl,
            Description: _description,
            Icon: _icon,
            InstallPathResolver: _installPathResolver,
            Elevation: _elevation,
            ExecutableName: _executableName,
            Shortcuts: _shortcuts,
            FileAssociations: _fileAssociations,
            AutoStart: _autoStart,
            PathRegistration: _pathRegistration,
            Prerequisites: _prerequisites,
            UserSteps: specs,
            Pages: pageSpecs,
            RegistryWrites: _registryWrites,
            Logging: _loggingBuilder ?? new LoggingBuilder(),
            DeclaredCliFlags: flagsWithMaps,
            PreviewEnabled: _previewEnabled,
            PayloadFilter: _payloadFilter is { HasAnyRules: true } pfb ? pfb.Build() : null,
            PublisherKeys: _publisherKeys.ToArray(),
            AllowUnsignedUpdates: _allowUnsignedUpdates,
            AllowInsecureServer: _allowInsecureServer,
            OfferLaunchAfterInstall: _offerLaunchAfterInstall,
            OfferNewerVersion: _offerNewerVersion,
            AllowVersionSelection: _allowVersionSelection,
            BrandImage: _brandImage,
            DownloadToken: _downloadToken,
            Migrations: migrations,
            AppManagedRunValues: _appManagedRunValues.ToArray());

        return new InstellaInstallerImpl(config);
    }

    private static bool SameFlag(string a, string b) =>
        string.Equals(a.TrimStart('-'), b.TrimStart('-'), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves a <c>MapCliFlag</c> key (<c>pageId.widgetId</c>, or an unqualified widget id
    /// that exactly one page declares) and checks the widget can hold the flag's type:
    /// bool → CheckBox; string → TextInput, Dropdown, RadioGroup, FolderPicker, FilePicker.
    /// </summary>
    private static string ResolvePageStateKey(CliFlagSpec flag, string key, IReadOnlyList<PageSpec> pages)
    {
        string where = $"InstallerBuilder.Build(): MapCliFlag(\"{flag.Name}\", \"{key}\")";
        List<(PageSpec Page, Widget Widget)> matches;
        var dot = key.IndexOf('.', StringComparison.Ordinal);
        if (dot > 0)
        {
            var pageId = key[..dot];
            var widgetId = key[(dot + 1)..];
            var page = pages.FirstOrDefault(p => p.Id == pageId)
                ?? throw new InvalidOperationException($"{where}: no page '{pageId}'.");
            matches = page.Widgets.Where(w => w.Id == widgetId).Select(w => (page, w)).ToList();
            if (matches.Count == 0)
                throw new InvalidOperationException($"{where}: page '{pageId}' has no widget '{widgetId}'.");
        }
        else
        {
            matches = pages.SelectMany(p => p.Widgets.Where(w => w.Id == key).Select(w => (p, w))).ToList();
            if (matches.Count == 0)
                throw new InvalidOperationException($"{where}: no page declares a widget '{key}'.");
            if (matches.Count > 1)
                throw new InvalidOperationException(
                    $"{where}: '{key}' is ambiguous ({string.Join(", ", matches.Select(m => m.Page.Id))}); qualify it as 'pageId.{key}'.");
        }

        var (target, widget) = matches[0];
        var compatible = flag.ValueType == typeof(bool)
            ? widget is CheckBox
            : flag.ValueType == typeof(string) && widget is TextInput or Dropdown or RadioGroup or FolderPicker or FilePicker;
        if (!compatible)
            throw new InvalidOperationException(
                $"{where}: a {flag.ValueType.Name} flag cannot fill a {widget.GetType().Name} (bool → CheckBox; string → TextInput, Dropdown, RadioGroup, FolderPicker, FilePicker).");
        return $"{target.Id}.{widget.Id}";
    }
}
