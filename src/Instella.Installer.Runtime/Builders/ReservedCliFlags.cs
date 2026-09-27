using System;
using System.Collections.Generic;

namespace Instella.Installer.Runtime.Builders;

/// <summary>
/// CLI flag names reserved by the installer runtime. User code trying to
/// register any of these via <see cref="InstallerBuilder.AddCliFlag{T}"/>
/// triggers a build-time error; unknown flags declared by the user are
/// validated against this list inside <see cref="InstallerBuilder.Build"/>.
/// </summary>
internal static class ReservedCliFlags
{
    /// <summary>
    /// Canonical set of reserved CLI flag names (leading <c>--</c> included), compared
    /// case-insensitively.
    /// </summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "--silent",
        "--path",
        "--install",
        "--update",
        "--uninstall",
        "--manage",
        "--cleanup",
        "--recover",
        // Guarded cleanup started by uninstall.
        "--token",
        "--parent-pid",
        // Install over an existing installation.
        "--allow-downgrade",
        "--force",
        "--force-close",
        "--no-newer-check",
        "--list-versions",
        "--app-version",
        "--choose-version",
        // Scope and UAC relaunch.
        "--scope",
        "--elevated-child",
        "--help",
        "--log-level",
        // update-family built-ins reserved by the update flow (UpdaterArgs). The server
        // and package id are read from the installed manifest, never the command line.
        "--app-path",
        "--app-exe",
        "--from-version",
        "--to-version",
        "--channel",
        "--use-patch",
        "--patch-sha256",
        "--allow-force-close",
        "--restart",
        "--no-restart",
        "--restart-countdown",
        "--graceful-timeout",
        "--extra-args",
        "--repair",
        // build-time manifest emission (the Instella.Installer.Build targets run the installer with it).
        "--emit-manifest",
        // Preview mode: walks through the installer's UI without
        // touching the host. Opt-in via InstallerBuilder.EnablePreview().
        "--preview",
        "--preview-mode",
        "--preview-speed",
        "--preview-fail",
    };
}
