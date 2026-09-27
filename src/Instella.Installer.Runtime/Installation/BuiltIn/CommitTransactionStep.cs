using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.Installation;
using Instella.Installer.Runtime.Core.Transactions;

namespace Instella.Installer.Runtime.Installation.BuiltIn;

/// <summary>
/// Swaps the staged payload, stub and installed manifest into place. Runs at
/// the start of <see cref="InstallStage.Finalize"/>, after <c>write-manifest</c> has staged
/// the installed manifest. Every user Finalize step and every point-of-no-return step is
/// ordered after it, so what they see under the install path is the new version, and when
/// rollback stops at a point of no return the installed manifest is already committed.
/// </summary>
internal sealed class CommitTransactionStep : IInstallStepExecution
{
    public const string StepName = "commit-transaction";

    public string Name => StepName;
    public InstallStage Stage => InstallStage.Finalize;
    public int Weight => 2;

    public async Task<StepResult> ExecuteAsync(InstallContext context, IStepProgress progress, CancellationToken cancellationToken)
    {
        if (context.Transaction is not { } txn)
            return StepResult.Fail("no install transaction to commit (extract-payload did not run)");

        // Re-hashing every staged file takes seconds for a large app (antivirus scans each
        // freshly written file as it is read), so both halves report per file.
        var clock = Stopwatch.StartNew();
        if (txn.State == TxnState.Staging)
        {
            const string verifying = "verifying files";
            progress.Report(0.0, verifying);
            await txn.VerifyAsync(cancellationToken, (done, total) => progress.Report(0.5 * done / total, verifying));
            context.Log.Info($"commit: verified the staged files in {clock.ElapsedMilliseconds} ms");
            clock.Restart();
        }

        // Commit ignores cancellation: once the first rename happens it must not stop half-way.
        // A failure part-way through is undone by extract-payload's rollback, which inspects
        // the file system.
        const string moving = "moving files into place";
        progress.Report(0.5, moving);
        await txn.CommitAsync((done, total) => progress.Report(0.5 + 0.5 * done / total, moving));
        context.Log.Info($"commit: moved the files into place in {clock.ElapsedMilliseconds} ms");
        progress.Report(1.0);
        return StepResult.Ok;
    }

    /// <summary>Puts the previous files (or none, for a first install) back.</summary>
    public async Task RollbackAsync(InstallContext context, CancellationToken cancellationToken)
    {
        if (context.Transaction is { } txn)
            await txn.RollbackAsync();
    }
}
