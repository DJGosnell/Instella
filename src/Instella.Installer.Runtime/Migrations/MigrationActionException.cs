using System;

namespace Instella.Installer.Runtime.Migrations;

/// <summary>
/// A migration action refused to act (the safety rules) or failed. It fails the migration: an
/// <see cref="MigrationTiming.AfterCommit"/> migration is logged as a warning and retried on the
/// next installer run; a <see cref="MigrationTiming.BeforeCommit"/> one fails the install.
/// </summary>
public sealed class MigrationActionException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="action">The action, for example <c>DeleteFiles</c>.</param>
    /// <param name="message">What was refused or failed, and why.</param>
    public MigrationActionException(string action, string message) : base($"{action}: {message}")
    {
        Action = action;
    }

    /// <summary>The action that refused or failed, for example <c>DeleteFiles</c>.</summary>
    public string Action { get; }
}
