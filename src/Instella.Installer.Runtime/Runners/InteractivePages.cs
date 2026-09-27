using System;
using System.Collections.Generic;
using Instella.Core.Installation;
using Instella.Installer.Runtime.Builders;
using Instella.Installer.Runtime.UI.Widgets;

namespace Instella.Installer.Runtime.Runners;

/// <summary>
/// Well-known <see cref="PageState"/> keys the interactive install flow
/// reads + writes. Centralised so the runner, the renderer wiring, and
/// the tests all share a single spelling.
/// </summary>
/// <remarks>
/// Only the <b>Progress</b> and <b>Status</b> keys are state-reactive today
/// — the renderers watch <see cref="PageState.StateChanged"/> and push the
/// new value to the progress-bar / status-line HWNDs. The <b>InstallPath</b>
/// and shortcut keys are user-input storage only (written by the Options
/// widgets, read by the runner after page navigation). The Complete / Error
/// pages bake their final text into the <see cref="PageSpec"/> at
/// construction time rather than using PageState binding, because today's
/// <see cref="Paragraph"/> / <see cref="ScrollableText"/> records are
/// immutable and there is no reactive-text widget.
/// </remarks>
internal static class InteractivePageStateKeys
{
    public const string InstallPath = "install-path";
    public const string ShortcutDesktop = "shortcut-desktop";
    public const string ShortcutStartMenu = "shortcut-startmenu";
    public const string Progress = "progress";
    public const string Status = "status";
    public const string CanFinish = "can-finish";
    public const string LaunchApp = "launch-app";
    public const string Failed = "failed";
    public const string ErrorDetails = "error-details";
}

/// <summary>
/// Well-known page ids for the synthetic pages the interactive flow
/// injects around the user's authored custom pages.
/// </summary>
internal static class InteractivePageIds
{
    public const string Welcome = "instella-welcome";
    public const string Options = "instella-options";
    public const string Progress = "instella-progress";
    public const string Complete = "instella-complete";
    public const string Error = "instella-error";
    public const string Refused = "instella-refused";
}

/// <summary>
/// Composes the wizard page list for the interactive install flow. Pure —
/// every page is produced deterministically from <see cref="FrozenConfig"/>
/// + the resolved <see cref="InstallerMode"/>. The runner takes the
/// returned list, constructs a parallel <see cref="PageState"/> list, and
/// hands both to the widget host.
/// </summary>
/// <remarks>
/// Standard-install page order:
/// <list type="number">
/// <item>Synthetic Welcome — app name, version, publisher, description.</item>
/// <item>User's custom pages from <see cref="FrozenConfig.Pages"/>, filtered
///   by <see cref="PageSpec.AllowedModes"/> against the current mode.</item>
/// <item>Synthetic Options — install-path folder picker + optional shortcut
///   checkboxes (only when the builder configured them).</item>
/// <item>Synthetic Progress — heading + progress bar + status line. The
///   runner drives both via <see cref="PageState"/> writes.</item>
/// <item>Synthetic Complete — built on demand by the runner after the step
///   pipeline succeeds, with the final install path baked into the paragraph.</item>
/// </list>
/// The synthetic Error page is built on demand too — NOT in the happy-path
/// list — so the Progress page's Back button can never land on it.
/// </remarks>
internal static class InteractivePages
{
    public static IReadOnlyList<PageSpec> BuildHappyPathPages(
        FrozenConfig config,
        InstallerMode mode,
        string defaultInstallPath,
        ExistingInstallation? existing = null,
        InstalledManifest? existingManifest = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrEmpty(defaultInstallPath);

        var pages = new List<PageSpec>
        {
            BuildWelcomePage(config, existing),
        };

        foreach (var userPage in config.Pages)
        {
            if (userPage.AllowedModes.Contains(mode))
                pages.Add(userPage);
        }

        pages.Add(BuildOptionsPage(config, defaultInstallPath, existingManifest));
        pages.Add(BuildProgressPage(config));

        return pages;
    }

    /// <summary>
    /// Build the Complete page with the final install path baked in. Called
    /// by the runner after the step pipeline succeeds and before navigating
    /// to this page.
    /// </summary>
    public static PageSpec BuildCompletePage(FrozenConfig config, string installPath)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrEmpty(installPath);
        return BuildPage(InteractivePageIds.Complete, p => p
            .Heading($"{config.AppName} has been installed")
            .Paragraph($"Installed to {installPath}.")
            .Paragraph("Click Finish to close the installer."));
    }

    /// <summary>
    /// Build the Error page with the failure message baked in. Called by
    /// the runner when a step fails and before navigating to this page.
    /// </summary>
    public static PageSpec BuildErrorPage(FrozenConfig config, string errorMessage, string? logFilePath)
    {
        ArgumentNullException.ThrowIfNull(config);
        var body = string.IsNullOrEmpty(errorMessage)
            ? "The installer was unable to complete. See the log for details."
            : errorMessage;
        return BuildPage(InteractivePageIds.Error, p =>
        {
            p.Heading("Installation failed");
            p.ScrollableText(body);
            if (!string.IsNullOrEmpty(logFilePath))
                p.Paragraph($"Log file: {logFilePath}");
        });
    }

    /// <summary>Height of the Welcome page's brand image, in 96-DPI pixels.</summary>
    internal const int WelcomeBrandImageHeight = 64;

    internal static PageSpec BuildWelcomePage(FrozenConfig config, ExistingInstallation? existing = null)
    {
        var publisherLine = string.IsNullOrEmpty(config.Publisher)
            ? $"Version {config.AppVersion}"
            : $"Version {config.AppVersion}. Published by {config.Publisher}.";
        var description = string.IsNullOrEmpty(config.Description)
            ? $"Click Continue to begin installing {config.AppName}."
            : config.Description;

        return BuildPage(InteractivePageIds.Welcome, p =>
        {
            // WithBrandImage(): the Instella logo unless the author chose another or none.
            if (config.BrandImage is { } brand)
                p.BrandImage(brand, maxHeight: WelcomeBrandImageHeight);
            p.Heading($"Welcome to {config.AppName}");
            p.Paragraph(publisherLine);
            if (existing is not null)
            {
                // Say which installation this run works on.
                var action = Instella.Core.Utilities.AppVersions.Equal(existing.Version, config.AppVersion) ? "repair" : "upgrade";
                p.Paragraph($"{config.AppName} {existing.Version} is installed in {existing.Path}. This installer will {action} it.");
            }
            p.Paragraph(description);
        });
    }

    /// <param name="config">The installer's configuration.</param>
    /// <param name="defaultInstallPath">The folder the picker starts with.</param>
    /// <param name="existing">The installation being upgraded or repaired: its choices are the defaults.</param>
    internal static PageSpec BuildOptionsPage(FrozenConfig config, string defaultInstallPath, InstalledManifest? existing = null)
    {
        return BuildPage(InteractivePageIds.Options, p =>
        {
            p.Heading("Installation options");
            p.FolderPicker(InteractivePageStateKeys.InstallPath, "Install to:", defaultInstallPath);

            if (config.Shortcuts is { Desktop: true })
                p.CheckBox(InteractivePageStateKeys.ShortcutDesktop, "Create a Desktop shortcut", defaultValue: existing?.HasDesktopShortcut ?? true);

            if (config.Shortcuts is { StartMenu: true })
                p.CheckBox(InteractivePageStateKeys.ShortcutStartMenu, "Add a Start Menu entry", defaultValue: existing?.HasStartMenuShortcut ?? true);

            // Continue allowed only when a non-empty install path has been
            // entered. The runner pre-seeds the default via widget-default
            // seeding before the host shows the page, so this is almost
            // always true the moment the user lands here.
            p.ContinueWhen(s => !string.IsNullOrEmpty(s.Text(InteractivePageStateKeys.InstallPath)));
        });
    }

    internal static PageSpec BuildProgressPage(FrozenConfig config)
    {
        return BuildPage(InteractivePageIds.Progress, p =>
        {
            p.Heading($"Installing {config.AppName}")
                .Paragraph("Please wait while the files are copied to your computer.")
                .Widget(new Progress { Id = InteractivePageStateKeys.Progress })
                .Widget(new StatusLine { Id = InteractivePageStateKeys.Status })
                // On failure: which step, why, whether it was rolled back, and the log. A
                // read-only multi-line box, so the text can be selected and copied.
                .Widget(new ScrollableText(string.Empty)
                {
                    Id = InteractivePageStateKeys.ErrorDetails,
                    Visible = s => s.Bool(InteractivePageStateKeys.Failed),
                });
            // WithLaunchAfterInstall(): read by the runner when the window closes after a
            // successful install; hidden after a failure.
            if (config.OfferLaunchAfterInstall is { } checkedByDefault)
                p.Widget(new CheckBox($"Launch {config.AppName} when I click Finish", checkedByDefault)
                {
                    Id = InteractivePageStateKeys.LaunchApp,
                    Visible = s => !s.Bool(InteractivePageStateKeys.Failed),
                });
            // Continue (labeled "Finish" as the last page) is locked until the
            // step pipeline finishes — the runner sets CanFinish=true from
            // its completion handler. Success / failure both unlock the button
            // so the user can close the window either way.
            p.ContinueWhen(s => s.Bool(InteractivePageStateKeys.CanFinish));
        });
    }

    /// <summary>
    /// Construct a <see cref="PageSpec"/> whose <see cref="PageSpec.AllowedModes"/>
    /// covers every <see cref="InstallerMode"/>. Synthetic pages are valid in
    /// any mode the interactive runner drives; narrower mode-gating is only
    /// relevant for user-authored pages.
    /// </summary>
    internal static PageSpec BuildPage(string id, Action<PageBuilder> configure)
    {
        var b = new PageBuilder(id);
        configure(b);
        b.InModes(
            InstallerMode.FirstInstall,
            InstallerMode.Upgrade,
            InstallerMode.Repair,
            InstallerMode.Update,
            InstallerMode.Uninstall,
            InstallerMode.Manage,
            InstallerMode.Cleanup,
            InstallerMode.Recover);
        return b.Build();
    }
}
