using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.Installation;
using Instella.Core.Platform;

namespace Instella.Installer.Runtime.Installation.BuiltIn;

/// <summary>
/// Creates each declared Windows service, or reconfigures the one this installation already
/// records, dependencies first, and records them for the installed manifest. A running service
/// keeps running the old files until <c>stop-services</c>: a configuration change only takes
/// effect at its next start.
/// </summary>
/// <remarks>
/// Rollback deletes what it created (stopping it first) and writes back the configuration it
/// snapshotted before each change, unless <c>stop-services</c>' rollback already did, then starts
/// whatever <c>stop-services</c> stopped and nothing has restarted yet, and revokes the "Log on
/// as a service" rights it granted.
/// <para>
/// A custom account (gMSA or <see cref="Runtime.Builders.ServiceAccount.FromInstaller"/>) is granted
/// <c>SeServiceLogonRight</c> when it lacks it, once per account, and the grant is recorded for the
/// installed manifest. A <c>FromInstaller</c> service gets a fresh copy of the run's password on each
/// create or change; with none (an upgrade that keeps the account) the service manager keeps the
/// stored secret. The password is zeroed when the step ends, whether it succeeded or not.
/// </para>
/// </remarks>
internal sealed class RegisterServicesStep(ServiceRun run) : IInstallStepExecution
{
    private readonly List<string> _created = [];
    private readonly List<string> _granted = [];

    public string Name => ServiceStepNames.RegisterServices;
    public InstallStage Stage => InstallStage.Register;
    public int Weight => 1;

    public async Task<StepResult> ExecuteAsync(InstallContext context, IStepProgress progress, CancellationToken cancellationToken)
    {
        try
        {
            return await RegisterAsync(context, progress, cancellationToken);
        }
        finally
        {
            context.ServiceCredentials?.ClearPassword();
        }
    }

    private async Task<StepResult> RegisterAsync(InstallContext context, IStepProgress progress, CancellationToken cancellationToken)
    {
        // Rights recorded by the existing installation stay recorded: uninstall revokes them.
        var granted = new List<string>(context.ExistingInstallation?.GrantedLogonRights ?? []);
        context.GrantedLogonRights = granted;
        var recorded = new List<ManifestService>();
        context.ManifestServices = recorded;
        if (run.Declared.Count == 0)
        {
            progress.Report(1.0);
            return StepResult.Ok;
        }

        // The runner refused a missing executable before anything changed; this catches a
        // payload it could not list.
        var extracted = context.ExtractedFiles.Select(f => f.RelativePath.Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var spec in run.Declared)
        {
            var rel = ServiceRun.RelativeExecutable(spec, context);
            if (extracted.Count > 0 && !extracted.Contains(rel))
                return StepResult.Fail($"service '{spec.Name}': executable '{rel}' is not in the payload");
        }

        if (await GrantLogonRightsAsync(context, granted, cancellationToken) is { } grantError)
        {
            await UndoAfterFailureAsync(context);
            return StepResult.Fail(grantError);
        }

        var scm = ServiceRun.Manager(context);
        var ordered = run.InStartOrder();
        for (var i = 0; i < ordered.Count; i++)
        {
            var spec = ordered[i];
            var configuration = ServiceRun.ConfigurationFor(spec, context);
            var previous = ServiceRun.Recorded(context, spec.Name);
            var existing = await scm.QueryConfigurationAsync(spec.Name, cancellationToken);
            string? error;
            if (existing.Outcome.Success)
            {
                if (previous is null)
                    error = $"a service named '{spec.Name}' already exists and is not part of this installation";
                else
                {
                    run.Snapshots[spec.Name] = existing.Value!;
                    var changed = await scm.ChangeConfigurationAsync(configuration, PasswordFor(spec, context), cancellationToken);
                    error = changed.Outcome.Success ? null : $"service '{spec.Name}' could not be reconfigured: {changed.Outcome.Error}";
                    if (error is null) context.Log.Info($"services: reconfigured '{spec.Name}' ({configuration.BinaryPath})");
                }
            }
            else if (existing.Failure == ServiceFailure.NotFound)
            {
                var created = await scm.CreateAsync(configuration, PasswordFor(spec, context), cancellationToken);
                error = created.Outcome.Success ? null : $"service '{spec.Name}' could not be created: {created.Outcome.Error}";
                if (error is null)
                {
                    _created.Add(spec.Name);
                    context.Log.Info($"services: created '{spec.Name}' ({configuration.BinaryPath})");
                }
            }
            else
            {
                error = $"service '{spec.Name}' could not be read: {existing.Outcome.Error}";
            }

            if (error is not null)
            {
                // The executor rolls back only the steps that completed: undo this one's work here.
                await UndoAfterFailureAsync(context);
                return StepResult.Fail(error);
            }

            recorded.Add(new ManifestService(spec.Name, ServiceRun.RelativeExecutable(spec, context), configuration.AccountName,
                spec.ManifestStartType, previous?.CreatedByInstella ?? true));
            progress.Report((i + 1) / (double)ordered.Count);
        }

        // The manifest lists them in declaration order.
        recorded.Sort((a, b) => IndexOf(a.Name).CompareTo(IndexOf(b.Name)));
        return StepResult.Ok;
    }

    /// <summary>A fresh copy of the run's password for a <c>FromInstaller</c> service (the seam zeroes it), or null.</summary>
    private static char[]? PasswordFor(Runtime.Builders.ServiceSpec spec, InstallContext context) =>
        spec.Account.NeedsPassword ? context.ServiceCredentials?.CopyPassword() : null;

    /// <summary>
    /// Grants "Log on as a service" to each distinct custom account that lacks it, and records it in
    /// <paramref name="granted"/>; returns why it failed, or null. Built-in and virtual accounts never
    /// reach the LSA.
    /// </summary>
    private async Task<string?> GrantLogonRightsAsync(InstallContext context, List<string> granted, CancellationToken ct)
    {
        var accounts = run.Declared
            .Where(s => s.Account.NeedsLogonRight)
            .Select(s => ServiceRun.AccountNameFor(s, context))
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (accounts.Count == 0) return null;

        var rights = context.LogonRights ?? throw new InvalidOperationException("no logon-rights service was given to the install");
        foreach (var account in accounts)
        {
            var query = await rights.HasAsync(account, ct);
            if (!query.Outcome.Success)
                return $"could not read whether '{account}' may log on as a service: {query.Outcome.Error}";
            if (query.Has) continue;
            var grant = await rights.GrantAsync(account, ct);
            if (!grant.Success)
                return $"could not grant '{account}' the right to log on as a service (SeServiceLogonRight): {grant.Error}";
            _granted.Add(account);
            if (!granted.Contains(account, StringComparer.OrdinalIgnoreCase)) granted.Add(account);
            context.Log.Info($"services: granted '{account}' the right to log on as a service");
        }
        return null;
    }

    private int IndexOf(string name)
    {
        for (var i = 0; i < run.Declared.Count; i++)
            if (string.Equals(run.Declared[i].Name, name, StringComparison.OrdinalIgnoreCase)) return i;
        return int.MaxValue;
    }

    private async Task UndoAfterFailureAsync(InstallContext context)
    {
        try
        {
            await RollbackAsync(context, CancellationToken.None);
        }
        catch (Exception ex)
        {
            context.Log.Warn($"{Name}: {ex.Message}");
        }
    }

    public async Task RollbackAsync(InstallContext context, CancellationToken cancellationToken)
    {
        var scm = ServiceRun.Manager(context);
        var problems = new List<string>();
        for (var i = _created.Count - 1; i >= 0; i--)
        {
            var name = _created[i];
            var (exists, stopped) = await ServiceRun.StateAsync(scm, name, cancellationToken);
            if (!exists) continue;
            if (!stopped)
            {
                var stop = await scm.StopAsync(name, run.Find(name)?.StopTimeout ?? Runtime.Builders.ServiceSpec.DefaultStopTimeout, cancellationToken);
                if (!stop.Outcome.Success) problems.Add($"could not stop service '{name}': {stop.Outcome.Error}");
            }
            var deleted = await scm.DeleteAsync(name, cancellationToken);
            if (deleted.Outcome.Success)
                context.Log.Info($"services: deleted '{name}', which this install had created");
            else
                problems.Add($"could not delete service '{name}': {deleted.Outcome.Error}");
        }
        _created.Clear();

        await run.RestoreSnapshotsAsync(context, problems, cancellationToken);
        await run.RestartStoppedAsync(context, problems, cancellationToken);

        // Only the rights this run granted: one the account already had is left alone.
        if (_granted.Count > 0 && context.LogonRights is { } rights)
        {
            for (var i = _granted.Count - 1; i >= 0; i--)
            {
                var account = _granted[i];
                var revoked = await rights.RevokeAsync(account, cancellationToken);
                if (revoked.Success)
                    context.Log.Info($"services: revoked the right to log on as a service this install had granted '{account}'");
                else
                    problems.Add($"could not revoke the right to log on as a service from '{account}': {revoked.Error}");
                context.GrantedLogonRights?.RemoveAll(a => string.Equals(a, account, StringComparison.OrdinalIgnoreCase));
            }
            _granted.Clear();
        }
        ServiceRun.ThrowIfProblems(context, Name, problems);
    }
}
