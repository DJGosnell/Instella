using System.Threading;
using System.Threading.Tasks;

namespace Instella.Core.Logging;

/// <summary>
/// Destination for <see cref="InstellaLogEntry"/> records. User-supplied sinks
/// are registered via <see cref="LoggingBuilder.AddSink(IInstellaLogSink)"/>;
/// the default file sink at <c>%TEMP%</c> is wired automatically unless
/// disabled.
/// </summary>
public interface IInstellaLogSink
{
    /// <summary>Writes one entry. Must not throw.</summary>
    void Write(in InstellaLogEntry entry);

    /// <summary>
    /// Flush any buffered entries. Called during installer shutdown; also on
    /// failure to ensure diagnostic context reaches disk before exit.
    /// </summary>
    ValueTask FlushAsync(CancellationToken cancellationToken);
}
