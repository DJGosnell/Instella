using System;
using System.Collections.Generic;

namespace Instella.Installer.Runtime.Builders;

/// <summary>
/// Parsed, typed view of the CLI arguments declared via
/// <see cref="InstallerBuilder.AddCliFlag{T}"/>. Instantiated by
/// <c>CliArgParser</c> and exposed to custom steps via
/// <see cref="Installation.InstallContext.Cli"/>.
/// </summary>
public sealed class CliArgs
{
    private readonly IReadOnlyDictionary<string, object?> _values;
    private readonly IReadOnlySet<string> _provided;

    internal CliArgs(
        IReadOnlyDictionary<string, object?> values,
        IReadOnlySet<string> provided,
        bool isSilent,
        IReadOnlyList<string> raw)
    {
        _values = values;
        _provided = provided;
        IsSilent = isSilent;
        Raw = raw;
    }

    /// <summary>No declared flags, nothing provided (contexts built outside a CLI run).</summary>
    internal static CliArgs Empty { get; } = new(
        new Dictionary<string, object?>(), new HashSet<string>(), isSilent: false, Array.Empty<string>());

    /// <summary>
    /// Options a non-strict parse skipped because it does not know them (the lenient
    /// maintenance modes log them: they may come from a newer SDK).
    /// </summary>
    internal IReadOnlyList<string> UnknownFlags { get; init; } = [];

    /// <summary>True if <c>--silent</c> was on the command line.</summary>
    public bool IsSilent { get; }

    /// <summary>Verbatim <c>string[] args</c> passed into <c>RunAsync</c>.</summary>
    public IReadOnlyList<string> Raw { get; }

    /// <summary>
    /// Typed accessor. Throws when <paramref name="name"/> was not declared
    /// on the builder — user code should never ask for a flag it didn't
    /// register.
    /// </summary>
    public T Get<T>(string name)
    {
        if (!_values.TryGetValue(Normalize(name), out var value))
            throw new InvalidOperationException($"Flag '{name}' was not declared via AddCliFlag<T>.");
        return value is T typed ? typed : default!;
    }

    /// <summary>Typed accessor with a caller-supplied default when not declared or not provided.</summary>
    public T Get<T>(string name, T @default)
    {
        if (_values.TryGetValue(Normalize(name), out var value) && value is T typed)
            return typed;
        return @default;
    }

    /// <summary>Gets the value of flag <paramref name="name"/> when it was declared, provided and is a <typeparamref name="T"/>.</summary>
    public bool TryGet<T>(string name, out T value)
    {
        if (_values.TryGetValue(Normalize(name), out var raw) && raw is T typed)
        {
            value = typed;
            return true;
        }
        value = default!;
        return false;
    }

    /// <summary>True if the caller actually passed <c>--name</c> or <c>--name value</c>.</summary>
    public bool WasProvided(string name) => _provided.Contains(Normalize(name));

    private static string Normalize(string name)
        => name.StartsWith("--", StringComparison.Ordinal) ? name : "--" + name;
}
