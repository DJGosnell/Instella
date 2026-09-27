using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.Installation;
using Instella.Core.Internal;

namespace Instella.Installer.Runtime.Installation.BuiltIn;

/// <summary>
/// Stages the stub as <c>{InstallPath}/<see cref="InstellaOwnedPaths.StubFileName"/></c>, so the
/// "Installed Apps" entry and later <c>--uninstall</c> / <c>--update</c> / <c>--manage</c>
/// invocations have a stable binary to point at. The stub is the separately signed copy the
/// build put in the payload (<see cref="InstellaOwnedPaths.PayloadStub"/>); only a
/// payload without one (dev builds, server downloads) falls back to truncating the running
/// installer, which leaves a signed installer's stub with a broken signature. It is committed with the rest of the transaction; running an
/// upgrade or repair installer is therefore what refreshes the stub (updates never do).
/// Must run before <see cref="RegisterUninstallEntryStep"/>.
/// </summary>
internal sealed class StageUninstallerStubStep : IInstallStepExecution
{
    /// <summary>Name of the stub placed in the install directory.</summary>
    public static string UninstallExeName => InstellaOwnedPaths.StubFileName;

    public string Name => "stage-uninstaller-stub";
    public InstallStage Stage => InstallStage.Register;

    public async Task<StepResult> ExecuteAsync(InstallContext context, IStepProgress progress, CancellationToken cancellationToken)
    {
        try
        {
            await using var stub = OpenPayloadStub(context) ?? OpenTruncatedSelf(context);
            if (context.Transaction is { } txn)
            {
                await txn.StageOwnedFileAsync(UninstallExeName, stub, executable: true, cancellationToken);
            }
            else
            {
                var destination = Path.Combine(context.InstallPath, UninstallExeName);
                var open = await context.FileSystem.OpenWriteAsync(destination, cancellationToken);
                if (!open.Success || open.Value is null)
                    throw new IOException(open.Error?.Message ?? "cannot write the stub");
                await using (open.Value)
                    await stub.CopyToAsync(open.Value, cancellationToken);
                context.TrackFile(destination);
            }
            progress.Report(1.0);
            return StepResult.Ok;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            context.Log.Warn($"stage-uninstaller-stub: {ex.Message} — ARP entry will be skipped");
            // Non-fatal: RegisterUninstallEntryStep sees no stub and skips cleanly (for
            // example dev builds with no appended footer).
            return StepResult.OkWithWarnings(new[] { $"uninstaller stub not staged: {ex.Message}" });
        }
    }

    /// <summary>The stub the payload carries, or null when it carries none.</summary>
    private static Stream? OpenPayloadStub(InstallContext context)
    {
        if (context.PayloadArchive is not { CanSeek: true } archive)
            return null;
        archive.Seek(0, SeekOrigin.Begin);
        var zip = new System.IO.Compression.ZipArchive(archive, System.IO.Compression.ZipArchiveMode.Read, leaveOpen: true);
        var entry = zip.Entries.FirstOrDefault(e =>
            string.Equals(e.FullName.Replace('\\', '/'), InstellaOwnedPaths.PayloadStub, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            zip.Dispose();
            return null;
        }

        // Buffered: the transaction hashes while it copies, and the archive stream is shared.
        var copy = new MemoryStream();
        using (var source = entry.Open())
            source.CopyTo(copy);
        zip.Dispose();
        copy.Position = 0;
        return copy;
    }

    /// <summary>The running installer's bytes up to (not including) its appended payload.</summary>
    private static Stream OpenTruncatedSelf(InstallContext context)
    {
        context.Log.Warn("stage-uninstaller-stub: the payload carries no stub; staging a truncated copy of this installer " +
                         "(its signature, if any, does not survive)");
        return OpenStub();
    }

    /// <summary>The running installer's bytes up to (not including) its appended payload.</summary>
    private static Stream OpenStub()
    {
        var selfPath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrEmpty(selfPath))
            throw new InvalidOperationException("cannot determine running installer path");

        var source = new FileStream(selfPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            var payloadEnd = PayloadFooterReader.GetPayloadEndOffset(source);
            var footer = PayloadFooterReader.ReadFooter(source, payloadEnd);
            var stubSize = footer.IsValid ? footer.ManifestOffset : source.Length;
            source.Seek(0, SeekOrigin.Begin);
            return new BoundedStream(source, stubSize);
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    /// <summary>Reads the first <c>length</c> bytes of a stream it owns.</summary>
    private sealed class BoundedStream(Stream inner, long length) : Stream
    {
        private long _read;
        private long Remaining => length - _read;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _read; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (Remaining <= 0) return 0;
            var read = inner.Read(buffer[..(int)Math.Min(buffer.Length, Remaining)]);
            _read += read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (Remaining <= 0) return 0;
            var read = await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, Remaining)], ct);
            _read += read;
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
