using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.Installation;
using Instella.Installer.Runtime.Installation;
using Instella.Installer.Runtime.UI.Widgets;

namespace Instella.Installer.Runtime.Builders;

/// <summary>
/// Frozen representation of a wizard page produced by
/// <see cref="PageBuilder.Build"/>. The installer runtime's page navigator
/// (Phases 13–15) consumes this in three passes:
/// <list type="bullet">
/// <item>When the page is about to become active, call <see cref="OnEnter"/>.</item>
/// <item>Render <see cref="Widgets"/> and consult <see cref="ContinueWhen"/>
///   to decide if the <c>Continue</c> button is enabled.</item>
/// <item>On <c>Continue</c>, invoke <see cref="OnValidate"/> first; if
///   <see cref="ValidationResult.IsValid"/> is false the error is surfaced
///   and navigation blocks. Otherwise call <see cref="OnLeave"/>.</item>
/// </list>
/// <see cref="When"/> and <see cref="AllowedModes"/> skip the page entirely.
/// </summary>
internal sealed record PageSpec(
    string Id,
    IReadOnlyList<Widget> Widgets,
    Func<PageState, bool>? ContinueWhen,
    Func<InstallContext, CancellationToken, Task>? OnEnter,
    Func<InstallContext, CancellationToken, Task>? OnLeave,
    Func<PageState, ValidationResult>? OnValidate,
    IReadOnlySet<InstallerMode> AllowedModes,
    Func<InstallContext, bool>? When)
{
    /// <summary>
    /// Label for the forward button on this page, instead of "Next &gt;" (or "Finish" on the
    /// last page). The scope page is a one-page window followed by the wizard, so it says Next.
    /// </summary>
    public string? ContinueLabel { get; init; }

    /// <summary>
    /// A label that follows the page's state (the update window's "Restart now" after success);
    /// null from it falls back to <see cref="ContinueLabel"/>.
    /// </summary>
    public Func<UI.Widgets.PageState, string?>? ContinueLabelFor { get; init; }
}
