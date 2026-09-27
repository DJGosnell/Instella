namespace Instella.Core.Logging;

/// <summary>
/// Severity of an <see cref="InstellaLogEntry"/>. Public values are stable;
/// enum underlying type is fixed at <see cref="byte"/> so wire / file
/// serialization is size-predictable.
/// </summary>
public enum InstellaLogLevel : byte
{
    /// <summary>Very detailed diagnostics.</summary>
    Trace = 0,
    /// <summary>Diagnostics useful when investigating a problem.</summary>
    Debug = 1,
    /// <summary>Normal progress.</summary>
    Info = 2,
    /// <summary>Something unexpected that did not stop the operation.</summary>
    Warn = 3,
    /// <summary>An operation failed.</summary>
    Error = 4,
    /// <summary>The installer cannot continue.</summary>
    Critical = 5,
}
