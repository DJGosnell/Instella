using System;
using Instella.Core.Logging;

namespace Instella.Installer.Runtime.Migrations;

/// <summary>Prefixes every line with <c>migration[&lt;id&gt;]: </c>, so the install log says which migration wrote it.</summary>
internal sealed class MigrationLogger(IInstellaLogger inner, string id) : IInstellaLogger
{
    private readonly string _prefix = $"migration[{id}]: ";

    public void Trace(string message) => inner.Trace(_prefix + message);
    public void Debug(string message) => inner.Debug(_prefix + message);
    public void Info(string message) => inner.Info(_prefix + message);
    public void Warn(string message) => inner.Warn(_prefix + message);
    public void Error(string message, Exception? exception = null) => inner.Error(_prefix + message, exception);
    public IDisposable Scope(string segment) => inner.Scope(segment);
    public bool IsEnabled(InstellaLogLevel level) => inner.IsEnabled(level);
}
