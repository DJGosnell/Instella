using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Instella.Installer.Runtime.Core.Processes;
using Instella.Installer.Testing;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.Services;

/// <summary>
/// The running-app gate never offers to close (or kills) the installation's own services: they are
/// stopped through the service manager instead. Only their process ids are ignored, so another copy
/// of the same executable is still found and closed.
/// </summary>
[TestFixture]
public class ServiceProcessGateTests
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "instella-service-gate", Guid.NewGuid().ToString("N"), "App");
    private InMemoryFileSystem _fs = null!;

    [SetUp]
    public void SetUp()
    {
        _fs = new InMemoryFileSystem();
        _fs.AddFile(Path.Combine(_root, "App.exe"), Encoding.UTF8.GetBytes("app"));
    }

    private sealed class Finder(params LockingProcess[] found) : ILockingProcessFinder
    {
        public IReadOnlyList<LockingProcess> Find(IReadOnlyList<string> files) => found;
    }

    private sealed class Closer : IProcessCloser
    {
        public List<int> Closed { get; } = [];
        public Task<bool> CloseAsync(LockingProcess process, TimeSpan timeout, CancellationToken ct)
        {
            Closed.Add(process.Id);
            return Task.FromResult(true);
        }
    }

    private RunningAppGate Gate(ILockingProcessFinder finder, IProcessCloser? closer = null, IReadOnlySet<int>? ignored = null) =>
        new(new FakePlatformServices(), new RecordingLogger(new RecordingSink()), finder, _fs, closer)
        {
            IgnoredProcessIds = ignored ?? new HashSet<int>(),
        };

    [Test]
    public void TheServicesProcess_IsNotABlocker_ButAnotherCopyOfTheSameExeIs()
    {
        var service = new LockingProcess(4242, "App.exe", CanClose: true);
        var desktopCopy = new LockingProcess(4243, "App.exe", CanClose: true);

        var blockers = Gate(new Finder(service, desktopCopy), ignored: new HashSet<int> { 4242 }).FindBlockers(_root, "App.exe");

        Assert.That(blockers.Select(b => b.Id), Is.EqualTo(new[] { 4243 }));
    }

    [Test]
    public async Task EnsureClosed_NeverClosesTheServicesProcess()
    {
        var closer = new Closer();
        var gate = Gate(new Finder(new LockingProcess(4242, "App.exe", CanClose: true)), closer, new HashSet<int> { 4242 });

        var ok = await gate.EnsureClosedAsync("App", _root, "App.exe", forceClose: true, prompt: null, CancellationToken.None);

        Assert.That(ok, Is.True);
        Assert.That(closer.Closed, Is.Empty);
    }

    [Test]
    public void ByDefault_NoProcessIsIgnored()
    {
        var gate = new RunningAppGate(new FakePlatformServices(), new RecordingLogger(new RecordingSink()),
            new Finder(new LockingProcess(4242, "App.exe", CanClose: true)), _fs);

        Assert.That(gate.IgnoredProcessIds, Is.Empty);
        Assert.That(gate.FindBlockers(_root, "App.exe").Select(b => b.Id), Is.EqualTo(new[] { 4242 }));
    }
}
