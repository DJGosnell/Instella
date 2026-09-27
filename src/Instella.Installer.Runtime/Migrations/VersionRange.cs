using System;
using System.Collections.Generic;
using Instella.Core.Utilities;

namespace Instella.Installer.Runtime.Migrations;

/// <summary>
/// A range of app versions for <c>UpgradingFrom</c>: comparators separated by spaces, all of which
/// must hold. A comparator is <c>&lt;</c>, <c>&lt;=</c>, <c>&gt;</c>, <c>&gt;=</c> or <c>=</c>
/// followed by a version; a bare version means <c>=</c>; <c>*</c> matches any version. Versions
/// compare canonically (<c>2</c>, <c>2.0</c> and <c>2.0.0</c> are equal). Example: <c>&gt;=1.0 &lt;2</c>.
/// </summary>
internal sealed class VersionRange
{
    private readonly List<(string Op, Version Version)> _comparators;
    private readonly string _text;

    private VersionRange(List<(string, Version)> comparators, string text)
    {
        _comparators = comparators;
        _text = text;
    }

    /// <summary>Parses <paramref name="text"/>.</summary>
    /// <exception cref="ArgumentException">The syntax is not valid.</exception>
    public static VersionRange Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("the version range is empty; use '*' for any version", nameof(text));

        var comparators = new List<(string, Version)>();
        var parts = new List<string>();
        foreach (var token in text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (token == "*")
            {
                parts.Add("*");
                continue;
            }
            var op = token.StartsWith(">=", StringComparison.Ordinal) ? ">="
                : token.StartsWith("<=", StringComparison.Ordinal) ? "<="
                : token.StartsWith('>') ? ">"
                : token.StartsWith('<') ? "<"
                : token.StartsWith('=') ? "="
                : "";
            var versionText = token[op.Length..];
            // A bare major ("2") means 2.0.0, as a reader would expect of ">=2".
            if (versionText.Length > 0 && !versionText.Contains('.')) versionText += ".0";
            if (!AppVersions.TryParse(versionText, out var version))
                throw new ArgumentException(
                    $"'{text}': '{token}' is not a comparator (<, <=, >, >=, = followed by a version such as 1.2.3, or *)", nameof(text));
            op = op.Length == 0 ? "=" : op;
            comparators.Add((op, version));
            parts.Add(op + AppVersions.ToCanonicalString(version));
        }
        return new VersionRange(comparators, string.Join(' ', parts));
    }

    /// <summary>Whether <paramref name="version"/> satisfies every comparator.</summary>
    public bool Contains(Version version)
    {
        foreach (var (op, bound) in _comparators)
        {
            var c = AppVersions.Compare(version, bound);
            var ok = op switch
            {
                "<" => c < 0,
                "<=" => c <= 0,
                ">" => c > 0,
                ">=" => c >= 0,
                _ => c == 0,
            };
            if (!ok) return false;
        }
        return true;
    }

    /// <summary>The range in canonical form, e.g. <c>&gt;=1.0.0 &lt;2.0.0</c>.</summary>
    public override string ToString() => _text;
}
