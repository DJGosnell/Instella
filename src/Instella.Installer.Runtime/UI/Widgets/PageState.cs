using System;
using System.Collections.Generic;

namespace Instella.Installer.Runtime.UI.Widgets;

/// <summary>
/// Reactive key/value store backing a single wizard page. Widgets read their
/// current value via <see cref="Get{T}(string, T)"/> (with typed overloads
/// for the common primitives), and write back via <see cref="Set(string, object?)"/>.
/// Only actual changes fire <see cref="StateChanged"/> — setting the same
/// value twice is a no-op — so predicates wired to the event don't run
/// unnecessarily.
/// </summary>
/// <remarks>
/// <para>Re-entry is a deliberate error. If a handler registered to
/// <see cref="StateChanged"/> tries to mutate state (via
/// <see cref="Set(string, object?)"/> or <see cref="Clear"/>), an
/// <see cref="InvalidOperationException"/> is thrown rather than quietly
/// allowing recursion. Widget visibility predicates and
/// <c>ContinueWhen</c> hooks must be pure reads.</para>
/// </remarks>
public sealed class PageState
{
    private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);
    private bool _isRaising;

    /// <summary>Fires whenever <see cref="Set(string, object?)"/> or <see cref="Clear"/> changes the effective contents.</summary>
    public event Action? StateChanged;

    /// <summary>Typed boolean accessor. Returns <paramref name="default"/> if the key is absent or stores a non-bool value.</summary>
    public bool Bool(string id, bool @default = false) => Get(id, @default);

    /// <summary>Typed string accessor. Returns <paramref name="default"/> if the key is absent or stores a non-string value.</summary>
    public string Text(string id, string @default = "") => Get(id, @default);

    /// <summary>
    /// Generic typed accessor. Returns <paramref name="default"/> when the
    /// key is absent or the stored value is not assignable to
    /// <typeparamref name="T"/>. A stored <see langword="null"/> is returned
    /// verbatim when <typeparamref name="T"/> is a reference or nullable
    /// value type, since callers that explicitly stored null want it back.
    /// </summary>
    public T Get<T>(string id, T @default = default!)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        if (_values.TryGetValue(id, out var raw))
        {
            if (raw is T typed) return typed;
            if (raw is null && default(T) is null) return default!;
        }
        return @default;
    }

    /// <summary>
    /// Generic typed accessor. Returns <see langword="true"/> when the key is
    /// present AND the stored value is assignable to <typeparamref name="T"/>
    /// (or is null for a nullable/reference <typeparamref name="T"/>);
    /// otherwise returns <see langword="false"/> and <paramref name="value"/>
    /// is the default.
    /// </summary>
    public bool TryGet<T>(string id, out T value)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        if (_values.TryGetValue(id, out var raw))
        {
            if (raw is T typed)
            {
                value = typed;
                return true;
            }
            if (raw is null && default(T) is null)
            {
                value = default!;
                return true;
            }
        }
        value = default!;
        return false;
    }

    /// <summary>
    /// Store <paramref name="value"/> under <paramref name="id"/>, firing
    /// <see cref="StateChanged"/> only if the new value differs from what
    /// was previously stored. <see langword="null"/> is a valid stored value.
    /// </summary>
    public void Set(string id, object? value)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        GuardReentry();

        if (_values.TryGetValue(id, out var existing) && Equals(existing, value))
            return;

        _values[id] = value;
        Raise();
    }

    /// <summary>Remove all entries. Fires <see cref="StateChanged"/> only if the store was non-empty.</summary>
    public void Clear()
    {
        GuardReentry();
        if (_values.Count == 0) return;
        _values.Clear();
        Raise();
    }

    /// <summary>Immutable snapshot of the current store. Safe to pass to long-running computations without races against further mutation.</summary>
    public IReadOnlyDictionary<string, object?> Snapshot()
        => new Dictionary<string, object?>(_values, StringComparer.Ordinal);

    private void GuardReentry()
    {
        if (_isRaising)
            throw new InvalidOperationException("PageState cannot be mutated from inside a StateChanged handler.");
    }

    private void Raise()
    {
        _isRaising = true;
        try { StateChanged?.Invoke(); }
        finally { _isRaising = false; }
    }
}
