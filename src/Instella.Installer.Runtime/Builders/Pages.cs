using System;

namespace Instella.Installer.Runtime.Builders;

/// <summary>
/// Convenience factories producing ready-made <c>Action&lt;PageBuilder&gt;</c>
/// delegates for the canonical wizard pages. Users combine these with their
/// own custom pages:
/// <code>
/// .AddPage("license", Pages.License(licenseText))
/// .AddPage("destination", Pages.Destination(defaultInstallPath))
/// </code>
/// </summary>
public static class Pages
{
    /// <summary>
    /// License-acceptance page: heading, scrollable text body, accept
    /// checkbox bound to <c>accepted</c>, <c>Continue</c> enabled only when
    /// the checkbox is checked.
    /// </summary>
    public static Action<PageBuilder> License(string licenseText)
    {
        ArgumentNullException.ThrowIfNull(licenseText);
        return p => p
            .Heading("License Agreement")
            .ScrollableText(licenseText)
            .CheckBox("accepted", "I accept the terms of the license agreement")
            .ContinueWhen(s => s.Bool("accepted"));
    }

    /// <summary>
    /// Destination-folder page: heading, folder picker bound to <c>path</c>,
    /// <c>Continue</c> enabled when a non-empty path is selected.
    /// </summary>
    public static Action<PageBuilder> Destination(string defaultPath)
    {
        return p => p
            .Heading("Install Location")
            .FolderPicker("path", "Install to:", defaultPath)
            .ContinueWhen(s => !string.IsNullOrWhiteSpace(s.Text("path")));
    }

    /// <summary>
    /// Read-only release-notes page: heading + scrollable body. No state is
    /// collected and <c>Continue</c> is always enabled.
    /// </summary>
    public static Action<PageBuilder> ReleaseNotes(string notesText)
    {
        ArgumentNullException.ThrowIfNull(notesText);
        return p => p
            .Heading("Release Notes")
            .ScrollableText(notesText);
    }
}
