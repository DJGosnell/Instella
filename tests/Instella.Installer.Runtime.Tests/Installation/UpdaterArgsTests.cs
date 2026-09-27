using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Instella.Core.Platform.Windows;
using Instella.Core.Update;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.Installation;

/// <summary>The updater's command line round-trips, including across the process boundary.</summary>
[TestFixture]
public partial class UpdaterArgsTests
{
    private static readonly string[] Tricky =
    [
        "plain", "with space", @"C:\Program Files\Acme\", "quote\"inside", @"trailing\\", "--looks-like-a-flag",
        "--extra-args", "", "tab\there", @"\\server\share\", "ünïcödé", "a=b", "-x",
    ];

    [Test]
    public void Parse_OfToArgumentList_IsIdentity([Range(0, 199)] int seed)
    {
        var original = Random(seed);
        var parsed = UpdaterArgs.Parse(original.ToArgumentList());
        Assert.That(parsed, Is.EqualTo(original), $"seed {seed}");
    }

    [Test]
    public void RoundTrip_AcrossTheWindowsProcessBoundary([Range(0, 49)] int seed)
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("CommandLineToArgvW is Windows-only");
        var original = Random(seed);

        var commandLine = "instella.exe " + WindowsCommandLine.Join(original.ToArgumentList());
        var parsed = UpdaterArgs.Parse(SplitWithShell32(commandLine).Skip(1).ToArray());

        Assert.That(parsed, Is.EqualTo(original), $"seed {seed}");
    }

    [Test]
    public void ToArgumentList_StartsWithUpdate_AndCarriesNoServerOrPackage()
    {
        var args = Random(1).ToArgumentList();
        Assert.That(args[0], Is.EqualTo("--update"));
        Assert.That(args, Does.Not.Contain("--server-url").And.Not.Contain("--package-id"));
        Assert.That(args, Does.Contain("--restart").Or.Contain("--no-restart"), "restart is always explicit");
    }

    [Test]
    public void Parse_IgnoresOptionsItDoesNotKnow()
    {
        // A newer SDK may add options; a 1.0 stub must still run the update (compatibility.md).
        var args = Random(3) with { ExtraArgs = ["--app-path", "not-this"] };
        var list = args.ToArgumentList().ToList();
        var extra = list.IndexOf("--extra-args");
        list.InsertRange(extra, ["--future-flag", "3", "--another", "--x=y"]);

        Assert.That(UpdaterArgs.Parse(list), Is.EqualTo(args));
    }

    [Test]
    public void Parse_RejectsMissingOrMalformedRequiredValues()
    {
        Assert.That(UpdaterArgs.Parse(["--update", "--app-path", "x", "--app-exe", "a.exe", "--from-version", "1.0"]), Is.Null);
        Assert.That(UpdaterArgs.Parse(["--app-path", "x", "--app-exe", "a", "--from-version", "one", "--to-version", "2.0"]), Is.Null);
    }

    private static UpdaterArgs Random(int seed)
    {
        var rng = new Random(seed);
        string Pick() => Tricky[rng.Next(Tricky.Length)] + (rng.Next(2) == 0 ? "" : rng.Next(1000).ToString());
        return new UpdaterArgs
        {
            AppPath = Pick(),
            AppExecutable = Pick(),
            FromVersion = new Version(rng.Next(10), rng.Next(10), rng.Next(10)),
            ToVersion = new Version(rng.Next(10), rng.Next(10), rng.Next(10), rng.Next(10)),
            Channel = rng.Next(2) == 0 ? "stable" : Pick(),
            UsePatch = rng.Next(2) == 0,
            PatchSha256 = rng.Next(2) == 0 ? null : new string('a', 64),
            AllowForceClose = rng.Next(2) == 0,
            GracefulTimeout = TimeSpan.FromSeconds(rng.Next(0, 300)),
            Restart = rng.Next(2) == 0,
            RestartCountdown = TimeSpan.FromSeconds(rng.Next(0, 60)),
            Silent = rng.Next(2) == 0,
            Repair = rng.Next(2) == 0,
            ParentPid = rng.Next(2) == 0 ? null : rng.Next(1, 100_000),
            ExtraArgs = Enumerable.Range(0, rng.Next(0, 5)).Select(_ => Pick()).ToList(),
        };
    }

    private static string[] SplitWithShell32(string commandLine)
    {
        var argv = CommandLineToArgvW(commandLine, out var argc);
        try
        {
            var result = new string[argc];
            for (var i = 0; i < argc; i++)
                result[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size))!;
            return result;
        }
        finally
        {
            LocalFree(argv);
        }
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr CommandLineToArgvW(string lpCmdLine, out int pNumArgs);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr LocalFree(IntPtr hMem);
}
