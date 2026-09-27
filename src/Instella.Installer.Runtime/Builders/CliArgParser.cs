using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Instella.Installer.Runtime.Builders;

/// <summary>
/// Parses <c>string[] args</c> into a typed <see cref="CliArgs"/> bag based
/// on the installer's declared flags. Reserved flags (<c>--silent</c>,
/// <c>--path</c>, ...) are consumed by <c>ModeDispatcher</c>; this parser
/// deals only with user-declared flags plus <c>--silent</c> detection.
/// </summary>
/// <remarks>
/// Forms accepted per flag: <c>--name value</c>, <c>--name=value</c>,
/// <c>--name</c> (boolean true), <c>--no-name</c> (boolean false). String-array
/// values are comma-separated. In strict mode (the installer's single parse in
/// <c>RunAsync</c>) a flag that is neither reserved nor declared, or a value of the
/// wrong type, throws <see cref="CliParseException"/>; everything after
/// <c>--extra-args</c> belongs to the app being updated and is not parsed.
/// </remarks>
internal static class CliArgParser
{
    public static CliArgs Parse(string[] args, IReadOnlyList<CliFlagSpec> declared) => Parse(args, declared, strict: false);

    public static CliArgs Parse(string[] args, IReadOnlyList<CliFlagSpec> declared, bool strict)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(declared);

        var specByName = declared.ToDictionary(f => Normalize(f.Name), StringComparer.OrdinalIgnoreCase);
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        var provided = new HashSet<string>(StringComparer.Ordinal);
        var isSilent = false;
        var unknown = new List<string>();

        // Initialize defaults.
        foreach (var spec in declared)
            values[Normalize(spec.Name)] = spec.DefaultValue;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "--silent") { isSilent = true; continue; }
            if (arg == "--extra-args") break;

            string? key = null;
            string? rawValue = null;

            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                var eq = arg.IndexOf('=', StringComparison.Ordinal);
                if (eq > 0)
                {
                    key = arg[..eq];
                    rawValue = arg[(eq + 1)..];
                }
                else
                {
                    key = arg;
                }
            }
            else
            {
                continue;
            }

            // --no-name for boolean negation.
            var boolNegation = key.StartsWith("--no-", StringComparison.Ordinal);
            var lookupKey = boolNegation ? "--" + key["--no-".Length..] : key;

            if (!specByName.TryGetValue(lookupKey, out var spec))
            {
                // Reserved flags are handled by the dispatcher and mode runners.
                if (!ReservedCliFlags.All.Contains(key))
                {
                    if (strict) throw new CliParseException($"unknown option '{key}'");
                    unknown.Add(key);
                }
                continue;
            }

            object? parsedValue;
            if (spec.ValueType == typeof(bool))
            {
                if (boolNegation) parsedValue = false;
                else if (rawValue is null) parsedValue = true;
                else parsedValue = ParseBool(rawValue);
            }
            else
            {
                if (rawValue is null && i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    rawValue = args[++i];

                if (rawValue is null)
                {
                    if (strict) throw new CliParseException($"option '{key}' requires a value");
                    continue;
                }

                try
                {
                    parsedValue = Coerce(rawValue, spec.ValueType);
                }
                catch (FormatException)
                {
                    throw new CliParseException($"option '{key}' expects {spec.ValueType.Name}, got '{rawValue}'");
                }
                catch (OverflowException)
                {
                    throw new CliParseException($"option '{key}' value '{rawValue}' is out of range");
                }
            }

            values[Normalize(spec.Name)] = parsedValue;
            provided.Add(Normalize(spec.Name));
        }

        return new CliArgs(values, provided, isSilent, args) { UnknownFlags = unknown };
    }

    private static object Coerce(string raw, Type t)
    {
        if (t == typeof(string)) return raw;
        if (t == typeof(int)) return int.Parse(raw, CultureInfo.InvariantCulture);
        if (t == typeof(long)) return long.Parse(raw, CultureInfo.InvariantCulture);
        if (t == typeof(bool)) return ParseBool(raw);
        if (t == typeof(string[])) return raw.Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (t == typeof(FileInfo)) return new FileInfo(raw);
        if (t == typeof(DirectoryInfo)) return new DirectoryInfo(raw);
        throw new InvalidOperationException($"Unsupported CLI flag type {t}");
    }

    private static bool ParseBool(string raw)
        => raw.Equals("true", StringComparison.OrdinalIgnoreCase)
           || raw == "1"
           || raw.Equals("yes", StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string name)
        => name.StartsWith("--", StringComparison.Ordinal) ? name : "--" + name;
}

/// <summary>The command line is not valid for this installer (exit 40).</summary>
internal sealed class CliParseException(string message) : Exception(message);
