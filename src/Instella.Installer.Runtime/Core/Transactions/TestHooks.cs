namespace Instella.Installer.Runtime.Core.Transactions;

/// <summary>
/// Fault injection for the end-to-end crash-recovery test. Compiled in only when
/// the runtime is built with <c>-p:InstellaTestHooks=true</c> (<c>INSTELLA_TEST_HOOKS</c>); in
/// every other build the calls are empty and inlined away.
/// </summary>
internal static class TestHooks
{
    /// <summary>
    /// <c>INSTELLA_TEST_FAULT=commit-after:N</c> kills the process right after the N-th live
    /// rename of a commit: no finally blocks, no rollback, as if the power went out.
    /// </summary>
    public static void AfterCommitRename(int renames)
    {
#if INSTELLA_TEST_HOOKS
        const string prefix = "commit-after:";
        var fault = Environment.GetEnvironmentVariable("INSTELLA_TEST_FAULT");
        if (fault is not null && fault.StartsWith(prefix, StringComparison.Ordinal)
            && int.TryParse(fault.AsSpan(prefix.Length), out var at) && at == renames)
            System.Diagnostics.Process.GetCurrentProcess().Kill();
#else
        _ = renames;
#endif
    }
}
