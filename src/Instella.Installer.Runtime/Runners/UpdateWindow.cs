using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Instella.Installer.Runtime.Builders;
using Instella.Installer.Runtime.Core.Update;
using Instella.Installer.Runtime.UI.Widgets;

namespace Instella.Installer.Runtime.Runners;

/// <summary>
/// The small window an interactive update shows: a heading, a progress bar, a
/// status line and Cancel, built from the same widget host as the wizard. Cancel works until
/// the commit starts; after that the engine finishes (or rolls back) on its own. On failure the
/// window stays open with the error. On success it stays open with "{App} was updated to {v}":
/// with a restart it counts down (<see cref="Instella.Core.Update.UpdaterArgs.RestartCountdown"/>)
/// and offers "Restart now"; closing it any way restarts too, because the user asked to update a
/// running app. Without a restart it offers "Close". A zero countdown closes at once.
/// </summary>
internal static class UpdateWindow
{
    /// <summary>State key: the update succeeded (switches the heading and the button).</summary>
    internal const string Succeeded = "update-succeeded";

    /// <param name="engine">The update to run.</param>
    /// <param name="appName">For the title and heading.</param>
    /// <param name="toVersion">For the heading.</param>
    /// <param name="repair">A repair, not an update.</param>
    /// <param name="hostFactory">The window host.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="restart">Restart the app after a success.</param>
    /// <param name="countdown">How long to count down before restarting.</param>
    /// <param name="onRestart">Starts the app: called once, after the window has closed, on success with a restart.</param>
    public static async Task<UpdateResult> RunAsync(
        UpdaterEngine engine, string appName, Version toVersion, bool repair, InteractiveHostFactory hostFactory,
        CancellationToken ct, bool restart = false, TimeSpan countdown = default, Action<UpdateResult>? onRestart = null)
    {
        var page = InteractivePages.BuildPage("instella-update", p => p
            .Widget(new Heading(repair ? $"Repairing {appName}" : $"Updating {appName} to {toVersion}")
                { Visible = s => !s.Bool(Succeeded) })
            .Widget(new Heading(repair ? $"{appName} was repaired" : $"{appName} was updated to {toVersion}")
                { Visible = s => s.Bool(Succeeded) })
            .Widget(new Progress { Id = InteractivePageStateKeys.Progress })
            .Widget(new StatusLine { Id = InteractivePageStateKeys.Status })
            .ContinueWhen(s => s.Bool(InteractivePageStateKeys.CanFinish))) with
        {
            ContinueLabelFor = s => s.Bool(Succeeded) ? (restart ? "&Restart now" : "&Close") : null,
        };
        var state = new PageState();
        using var host = hostFactory(appName, new List<PageSpec> { page }, new List<PageState> { state });

        var committing = false;
        var done = false;
        Timer? timer = null;
        engine.ProgressChanged += (_, p) => host.PostToUiThread(() =>
        {
            if (done) return;
            state.Set(InteractivePageStateKeys.Progress, Math.Clamp(p.Percentage / 100.0, 0, 1));
            if (p.StatusText is { } text) state.Set(InteractivePageStateKeys.Status, text);
        });
        engine.StateChanged += (_, s) =>
        {
            // From the commit on, cancelling is no longer possible: grey Cancel out.
            if (s is UpdaterState.Finalizing or UpdaterState.RollingBack && !committing)
            {
                committing = true;
                host.PostToUiThread(host.LockNavigation);
            }
        };
        host.CancelInterceptor = () =>
        {
            if (done) return false;          // the result is shown: closing just closes (and restarts)
            if (!committing) engine.Cancel();
            return true;                     // the window closes when the engine has stopped
        };

        var run = Task.Run(() => engine.RunAsync(ct), CancellationToken.None);
        _ = run.ContinueWith(t => host.PostToUiThread(() =>
        {
            var result = t.Result;
            done = true;
            if (result.WasCancelled || (result.Success && restart && countdown <= TimeSpan.Zero))
            {
                host.Close();
                return;
            }
            state.Set(InteractivePageStateKeys.CanFinish, true);
            host.LockNavigation();
            if (!result.Success)
            {
                state.Set(InteractivePageStateKeys.Status, $"{(repair ? "Repair" : "Update")} failed: {result.Error}");
                return;
            }

            state.Set(Succeeded, true);
            state.Set(InteractivePageStateKeys.Progress, 1.0);
            if (!restart)
            {
                state.Set(InteractivePageStateKeys.Status, "");
                return;
            }
            var deadline = DateTime.UtcNow + countdown;
            state.Set(InteractivePageStateKeys.Status, Countdown(countdown));
            var period = countdown < TimeSpan.FromSeconds(1) ? countdown : TimeSpan.FromSeconds(1);
            var closing = 0;
            timer = new Timer(_ => host.PostToUiThread(() =>
            {
                var left = deadline - DateTime.UtcNow;
                if (left > TimeSpan.Zero)
                    state.Set(InteractivePageStateKeys.Status, Countdown(left));
                else if (Interlocked.Exchange(ref closing, 1) == 0)
                    host.Close();
            }), null, period, period);
        }), TaskScheduler.Default);

        host.Run();
        var final = await run;
        timer?.Dispose();
        // Every way out of a successful update with a restart restarts: "Restart now", the
        // countdown, and closing the window.
        if (final.Success && restart) onRestart?.Invoke(final);
        return final;
    }

    private static string Countdown(TimeSpan left) =>
        $"Restarting in {Math.Max(1, (int)Math.Ceiling(left.TotalSeconds))} s…";
}
