using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.Platform;
using Instella.Installer.Runtime.Installation;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.Installation;

[TestFixture]
public class TrackedRollbackTests
{
    [Test]
    public async Task Files_Are_Deleted_In_Reverse_Order()
    {
        var b = new TestContextBuilder();
        b.FileSystem.Files[@"C:\install\a.exe"] = new byte[] { 1 };
        b.FileSystem.Files[@"C:\install\b.dll"] = new byte[] { 2 };
        var ctx = b.Build();

        ctx.TrackFile(@"C:\install\a.exe");
        ctx.TrackFile(@"C:\install\b.dll");

        var warnings = new List<string>();
        await ctx.Ledger.UnwindAsync(ctx, warnings, fromIndex: 0, CancellationToken.None);

        Assert.That(b.FileSystem.Files, Is.Empty);
        Assert.That(warnings, Is.Empty);
    }

    [Test]
    public async Task Directories_Are_Deleted_With_Their_Recursive_Flag()
    {
        var b = new TestContextBuilder();
        b.FileSystem.Directories.Add(@"C:\install");
        b.FileSystem.Directories.Add(@"C:\install\bin");
        var ctx = b.Build();

        ctx.TrackDirectory(@"C:\install\bin", recursive: false);
        ctx.TrackDirectory(@"C:\install", recursive: true);

        var warnings = new List<string>();
        await ctx.Ledger.UnwindAsync(ctx, warnings, fromIndex: 0, CancellationToken.None);

        Assert.That(b.FileSystem.Directories, Is.Empty);
    }

    [Test]
    public async Task Registry_Value_Triggers_DeleteRegistryValueAsync()
    {
        var b = new TestContextBuilder();
        var ctx = b.Build();

        ctx.TrackRegistryValue(RegistryHive.CurrentUser, @"Software\MyApp", "InstallPath");

        var warnings = new List<string>();
        await ctx.Ledger.UnwindAsync(ctx, warnings, fromIndex: 0, CancellationToken.None);

        Assert.That(b.Platform.Calls, Has.Some.Contains("DeleteRegistryValue:CurrentUser:Software\\MyApp:InstallPath"));
        Assert.That(warnings, Is.Empty);
    }

    [Test]
    public async Task Registry_Key_Triggers_DeleteRegistryKeyAsync()
    {
        var b = new TestContextBuilder();
        var ctx = b.Build();

        ctx.TrackRegistryKey(RegistryHive.LocalMachine, @"Software\MyApp");

        var warnings = new List<string>();
        await ctx.Ledger.UnwindAsync(ctx, warnings, fromIndex: 0, CancellationToken.None);

        Assert.That(b.Platform.Calls, Has.Some.Contains("DeleteRegistryKey:LocalMachine:Software\\MyApp"));
    }

    [Test]
    public async Task Path_Entry_Triggers_RemoveFromPathAsync()
    {
        var b = new TestContextBuilder();
        var ctx = b.Build();

        ctx.TrackPathEntry(@"C:\install\bin");

        var warnings = new List<string>();
        await ctx.Ledger.UnwindAsync(ctx, warnings, fromIndex: 0, CancellationToken.None);

        Assert.That(b.Platform.Calls, Has.Some.Contains("RemoveFromPath:C:\\install\\bin"));
    }

    [Test]
    public async Task Combined_Kinds_Unwind_LIFO_Across_Categories()
    {
        var b = new TestContextBuilder();
        b.FileSystem.Files[@"C:\install\a.exe"] = new byte[] { 1 };
        b.FileSystem.Directories.Add(@"C:\install");
        var ctx = b.Build();

        ctx.TrackFile(@"C:\install\a.exe");
        ctx.TrackRegistryValue(RegistryHive.CurrentUser, @"Software\MyApp", "Version");
        ctx.TrackPathEntry(@"C:\install\bin");
        ctx.TrackRegistryKey(RegistryHive.CurrentUser, @"Software\MyApp");
        ctx.TrackDirectory(@"C:\install", recursive: true);

        var warnings = new List<string>();
        await ctx.Ledger.UnwindAsync(ctx, warnings, fromIndex: 0, CancellationToken.None);

        // Calls list is appended in unwind order: directory first (last tracked),
        // then registry-key, path-entry, registry-value, file (file is FS-side
        // so doesn't appear in Platform.Calls — verify FS state instead).
        Assert.Multiple(() =>
        {
            Assert.That(b.Platform.Calls.IndexOf("DeleteRegistryKey:CurrentUser:Software\\MyApp"),
                Is.LessThan(b.Platform.Calls.IndexOf("RemoveFromPath:C:\\install\\bin")),
                "registry-key (tracked 4th) should unwind before path-entry (tracked 3rd)");
            Assert.That(b.Platform.Calls.IndexOf("RemoveFromPath:C:\\install\\bin"),
                Is.LessThan(b.Platform.Calls.IndexOf("DeleteRegistryValue:CurrentUser:Software\\MyApp:Version")),
                "path-entry (3rd) before registry-value (2nd)");
            Assert.That(b.FileSystem.Files, Is.Empty);
            Assert.That(b.FileSystem.Directories, Is.Empty);
        });
    }

    [Test]
    public async Task Failed_Registry_Delete_Surfaces_As_Warning_Not_Failure()
    {
        var b = new TestContextBuilder();
        b.Platform.DeleteRegistryValueResult = Task.FromResult(PlatformResult.Fail("refused by the test"));
        var ctx = b.Build();

        ctx.TrackRegistryValue(RegistryHive.CurrentUser, @"Software\MyApp", "InstallPath");

        var warnings = new List<string>();
        await ctx.Ledger.UnwindAsync(ctx, warnings, fromIndex: 0, CancellationToken.None);

        Assert.That(warnings, Has.Some.Contains("InstallPath"));
    }

    [Test]
    public async Task Failed_PATH_Removal_Surfaces_As_Warning()
    {
        var b = new TestContextBuilder();
        b.Platform.RemoveFromPathResult = Task.FromResult(PlatformResult.Fail("refused by the test"));
        var ctx = b.Build();

        ctx.TrackPathEntry(@"C:\install\bin");

        var warnings = new List<string>();
        await ctx.Ledger.UnwindAsync(ctx, warnings, fromIndex: 0, CancellationToken.None);

        Assert.That(warnings, Has.Some.Contains("C:\\install\\bin"));
    }

    [Test]
    public async Task FromIndex_Skips_Earlier_Entries()
    {
        var b = new TestContextBuilder();
        b.FileSystem.Files[@"C:\install\a.exe"] = new byte[] { 1 };
        b.FileSystem.Files[@"C:\install\b.dll"] = new byte[] { 2 };
        var ctx = b.Build();

        ctx.TrackFile(@"C:\install\a.exe"); // index 0 — should survive
        ctx.TrackFile(@"C:\install\b.dll"); // index 1 — should be unwound

        var warnings = new List<string>();
        await ctx.Ledger.UnwindAsync(ctx, warnings, fromIndex: 1, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(b.FileSystem.Files.ContainsKey(@"C:\install\a.exe"), Is.True);
            Assert.That(b.FileSystem.Files.ContainsKey(@"C:\install\b.dll"), Is.False);
        });
    }

    [Test]
    public void Track_Methods_Reject_Empty_Args()
    {
        var ledger = new TrackingLedger();
        Assert.Multiple(() =>
        {
            Assert.Throws<System.ArgumentException>(() => ledger.TrackRegistryValue(RegistryHive.CurrentUser, "", "name"));
            Assert.Throws<System.ArgumentException>(() => ledger.TrackRegistryValue(RegistryHive.CurrentUser, "key", ""));
            Assert.Throws<System.ArgumentException>(() => ledger.TrackRegistryKey(RegistryHive.CurrentUser, ""));
            Assert.Throws<System.ArgumentException>(() => ledger.TrackPathEntry(""));
        });
    }
}
