using System.Threading;
using System.Threading.Tasks;

namespace Instella.Installer.Runtime.Builders;

/// <summary>
/// The frozen installer produced by <see cref="InstallerBuilder.Build"/>.
/// The user's <c>*.Installer</c> project calls <see cref="RunAsync"/> from
/// its <c>Program.Main</c>; the returned <see cref="int"/> is the process exit
/// code (see <see cref="Instella.Core.Installation.InstellaExitCode"/>).
/// </summary>
public interface IInstellaInstaller
{
    /// <summary>
    /// Parse <paramref name="args"/>, dispatch to the appropriate mode runner,
    /// and return the mapped exit code. Safe to call from a synchronous
    /// <c>Main</c> via <c>.GetAwaiter().GetResult()</c>; asynchronous callers
    /// can flow <paramref name="ct"/> for cancellation.
    /// </summary>
    Task<int> RunAsync(string[] args, CancellationToken ct = default);
}
