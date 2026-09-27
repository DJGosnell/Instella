using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.Installation;
using Instella.Installer.Runtime.Builders;
using Instella.Installer.Runtime.Installation;
using Instella.Installer.Runtime.UI.Widgets;

namespace Instella.Installer.Runtime.Runners;

/// <summary>A silent run met a page it cannot answer without a UI (exit 14).</summary>
internal sealed class SilentMissingStateException(string message) : Exception(message);

/// <summary>
/// Page lifecycle shared by the interactive wizard and silent installs:
/// seed widget defaults, apply mapped CLI flags, run <c>OnEnter</c>, check
/// <c>OnValidate</c> and <c>ContinueWhen</c>, run <c>OnLeave</c>.
/// </summary>
internal static class PageFlow
{
    /// <summary>
    /// Seeds <paramref name="state"/> with the page's widget defaults, then overwrites each
    /// widget whose value was supplied through a mapped CLI flag (<c>MapCliFlag</c>).
    /// Interactive users see the flag values pre-filled; silent runs use them as answers.
    /// </summary>
    public static void InitializeState(PageSpec page, PageState state, CliArgs cli, IReadOnlyList<CliFlagSpec> flags)
    {
        PreviewModeRunner.SeedWidgetDefaults(page, state);
        foreach (var (widgetId, flag) in MappedFlags(page, flags))
        {
            if (cli.WasProvided(flag.Name) && cli.TryGet<object>(flag.Name, out var value))
                state.Set(widgetId, value);
        }
    }

    /// <summary>Flags mapped onto <paramref name="page"/>'s widgets, keyed by widget id.</summary>
    public static IEnumerable<(string WidgetId, CliFlagSpec Flag)> MappedFlags(PageSpec page, IReadOnlyList<CliFlagSpec> flags)
    {
        var prefix = page.Id + ".";
        foreach (var flag in flags)
        {
            if (flag.MapsTo is { } key && key.StartsWith(prefix, StringComparison.Ordinal))
                yield return (key[prefix.Length..], flag);
        }
    }

    /// <summary>True when <paramref name="page"/> is shown in <paramref name="mode"/> for this context.</summary>
    public static bool IsApplicable(PageSpec page, InstallerMode mode, InstallContext context) =>
        page.AllowedModes.Contains(mode)
        && (page.When is not { } when
            // A throwing When shows the page, where the failure is visible.
            || UserCode.Run(() => when(context), $"page '{page.Id}' When", context.Log, onError: true));

    /// <summary>
    /// Checks that <paramref name="page"/> may be left: <c>OnValidate</c>, then
    /// <c>ContinueWhen</c>. Returns why it may not, or null.
    /// </summary>
    public static string? BlockReason(PageSpec page, PageState state)
    {
        // A throwing OnValidate or ContinueWhen blocks the page: never wave the user through.
        if (page.OnValidate is { } validate
            && UserCode.Run(() => validate(state), $"page '{page.Id}' OnValidate", null,
                ValidationResult.Fail($"This page could not be checked: {page.Id} OnValidate failed.")) is { IsValid: false } invalid)
            return invalid.Error ?? "validation failed";
        if (page.ContinueWhen is { } continueWhen
            && !UserCode.Run(() => continueWhen(state), $"page '{page.Id}' ContinueWhen", null, onError: false))
            return UserCode.LastError ?? "its continue condition is not met";
        return null;
    }

    /// <summary>
    /// Answers every applicable user page without a UI, in order. Throws
    /// <see cref="SilentMissingStateException"/> naming the first page that would block and
    /// the flags that can answer it.
    /// </summary>
    public static async Task ResolveSilentlyAsync(
        FrozenConfig config, InstallerMode mode, CliArgs cli, InstallContext context,
        IDictionary<string, PageState> states, CancellationToken ct)
    {
        foreach (var page in config.Pages)
        {
            if (!IsApplicable(page, mode, context)) continue;

            var state = new PageState();
            states[page.Id] = state;
            InitializeState(page, state, cli, config.DeclaredCliFlags);
            if (page.OnEnter is { } onEnter) await onEnter(context, ct);

            if (BlockReason(page, state) is { } reason)
            {
                var mapped = MappedFlags(page, config.DeclaredCliFlags).Select(m => $"--{m.Flag.Name.TrimStart('-')}").ToList();
                var hint = mapped.Count > 0
                    ? $"pass {string.Join(" / ", mapped)}"
                    : $"map a flag onto it with MapCliFlag(\"<flag>\", \"{page.Id}.<widget>\") and pass that flag";
                throw new SilentMissingStateException($"page '{page.Id}' requires input ({reason}): {hint}.");
            }

            if (page.OnLeave is { } onLeave) await onLeave(context, ct);
        }
    }
}
