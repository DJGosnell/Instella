using System;
using System.Collections.Generic;
using System.Linq;

namespace Instella.Installer.Runtime.Migrations;

/// <summary>The <c>InstallerBuilder.Build()</c> checks on registered migrations, and their order.</summary>
internal static class MigrationValidation
{
    /// <summary>The prefix of migration step names; user steps may not use it.</summary>
    public const string StepPrefix = "migration:";

    /// <summary>
    /// Whether <paramref name="id"/> is a valid migration id: 1–64 characters of <c>a-z</c>,
    /// <c>0-9</c>, <c>.</c>, <c>_</c>, <c>-</c>, starting and ending with a letter or digit.
    /// </summary>
    public static bool IsValidId(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 64) return false;
        static bool Alnum(char c) => c is (>= 'a' and <= 'z') or (>= '0' and <= '9');
        if (!Alnum(id[0]) || !Alnum(id[^1])) return false;
        foreach (var c in id)
            if (!Alnum(c) && c is not ('.' or '_' or '-')) return false;
        return true;
    }

    /// <summary>
    /// Validates <paramref name="migrations"/> and returns them sorted by timing
    /// (<see cref="MigrationTiming.BeforeCommit"/>, <see cref="MigrationTiming.AfterCommit"/>,
    /// <see cref="MigrationTiming.Uninstall"/>), then <see cref="InstallMigration.Order"/>, then id.
    /// </summary>
    /// <exception cref="InvalidOperationException">A migration is not valid.</exception>
    public static IReadOnlyList<InstallMigration> ValidateAndSort(IReadOnlyList<InstallMigration> migrations)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in migrations)
        {
            var id = m.Id;
            var where = $"InstallerBuilder.Build(): migration '{id}' ({m.GetType().Name})";
            if (!IsValidId(id))
                throw new InvalidOperationException(
                    $"{where}: the id must be 1-64 characters of lowercase letters, digits, '.', '_' and '-', starting and ending with a letter or digit.");
            if (!ids.Add(id))
                throw new InvalidOperationException($"{where}: another migration has the same id; ids are permanent and unique.");
            if (string.IsNullOrWhiteSpace(m.DisplayName))
                throw new InvalidOperationException($"{where}: DisplayName is empty.");
            if (!Enum.IsDefined(m.Timing))
                throw new InvalidOperationException($"{where}: Timing {(int)m.Timing} is not a MigrationTiming.");
            if (m.Timing == MigrationTiming.Uninstall && m.RunOnce)
                throw new InvalidOperationException($"{where}: an uninstall migration runs on every uninstall; RunOnce must be false.");

            Condition condition;
            try
            {
                condition = m.GetCondition();
            }
            catch (InvalidOperationException ex)
            {
                throw new InvalidOperationException(
                    $"{where}: When() failed ({ex.Message}). When() may only compose conditions; read Context in Condition.From(...) or ExecuteAsync.", ex);
            }
            catch (ArgumentException ex)
            {
                throw new InvalidOperationException($"{where}: When() has an invalid condition: {ex.Message}", ex);
            }

            var possible = condition.PossibleModes & ModeSets.For(m.Timing);
            if (possible == ModeSet.None)
                throw new InvalidOperationException(
                    $"{where}: its condition ({condition}) can only be true in {condition.PossibleModes}, but {m.Timing} migrations run in {ModeSets.For(m.Timing)}; it would never run.");
        }

        return migrations
            .OrderBy(m => (int)m.Timing)
            .ThenBy(m => m.Order)
            .ThenBy(m => m.Id, StringComparer.Ordinal)
            .ToList();
    }
}
