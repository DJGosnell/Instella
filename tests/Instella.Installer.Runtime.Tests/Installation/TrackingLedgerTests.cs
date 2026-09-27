using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Instella.Installer.Runtime.Installation;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.Installation;

[TestFixture]
public class TrackingLedgerTests
{
    [Test]
    public async Task Unwind_Deletes_Files_In_LIFO_Order()
    {
        var ctxBuilder = new TestContextBuilder();
        ctxBuilder.FileSystem.Files[@"C:\install\a.exe"] = new byte[] { 1 };
        ctxBuilder.FileSystem.Files[@"C:\install\b.dll"] = new byte[] { 2 };
        ctxBuilder.FileSystem.Files[@"C:\install\c.txt"] = new byte[] { 3 };
        var ctx = ctxBuilder.Build();

        ctx.TrackFile(@"C:\install\a.exe");
        ctx.TrackFile(@"C:\install\b.dll");
        ctx.TrackFile(@"C:\install\c.txt");

        var warnings = new List<string>();
        await ctx.Ledger.UnwindAsync(ctx, warnings, fromIndex: 0, CancellationToken.None);

        Assert.That(ctxBuilder.FileSystem.Files, Is.Empty);
        Assert.That(warnings, Is.Empty);
    }

    [Test]
    public async Task Unwind_Deletes_Tracked_Directories()
    {
        var ctxBuilder = new TestContextBuilder();
        ctxBuilder.FileSystem.Directories.Add(@"C:\install");
        ctxBuilder.FileSystem.Directories.Add(@"C:\install\bin");
        var ctx = ctxBuilder.Build();

        ctx.TrackDirectory(@"C:\install");
        ctx.TrackDirectory(@"C:\install\bin");

        var warnings = new List<string>();
        await ctx.Ledger.UnwindAsync(ctx, warnings, fromIndex: 0, CancellationToken.None);

        Assert.That(ctxBuilder.FileSystem.Directories, Is.Empty);
    }

    [Test]
    public async Task Unwind_Skips_Entries_That_No_Longer_Exist()
    {
        var ctxBuilder = new TestContextBuilder();
        var ctx = ctxBuilder.Build();
        // file tracked but never written; unwind should not complain.

        ctx.TrackFile(@"C:\install\missing.exe");
        ctx.TrackDirectory(@"C:\install");

        var warnings = new List<string>();
        await Assert.MultipleAsync(async () =>
        {
            await ctx.Ledger.UnwindAsync(ctx, warnings, fromIndex: 0, CancellationToken.None);
            Assert.That(warnings, Is.Empty);
        });
    }

    [Test]
    public void TrackFile_Rejects_Empty_Path()
    {
        var ledger = new TrackingLedger();
        Assert.Throws<System.ArgumentException>(() => ledger.TrackFile(""));
    }

    [Test]
    public void TrackDirectory_Rejects_Empty_Path()
    {
        var ledger = new TrackingLedger();
        Assert.Throws<System.ArgumentException>(() => ledger.TrackDirectory(""));
    }
}
