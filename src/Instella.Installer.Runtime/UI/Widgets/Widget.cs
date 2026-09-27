using System;

namespace Instella.Installer.Runtime.UI.Widgets;

/// <summary>
/// Base of the widget record hierarchy. Every widget may opt-in to reactive
/// visibility and enabled-state by supplying predicates that read from the
/// current page's <see cref="PageState"/>. Predicates run on every state
/// change and must therefore be cheap and free of side effects.
/// </summary>
/// <remarks>
/// Widget records are value-typed: two widgets with the same fields are
/// equal, and <c>with</c>-expression updates produce new instances rather
/// than mutating in place. This gives page layouts a predictable,
/// diff-friendly shape that the page builder can freeze into a
/// <c>PageSpec</c> without defensive copying. The platform renderers
/// pattern-match on the closed hierarchy.
/// </remarks>
public abstract record Widget
{
    /// <summary>
    /// Logical identifier used by <see cref="PageState"/> to read and write
    /// the widget's value. Read-only widgets (<see cref="Heading"/>,
    /// <see cref="Paragraph"/>, <see cref="ScrollableText"/>,
    /// <see cref="BrandImage"/>, <see cref="Progress"/>,
    /// <see cref="StatusLine"/>) may leave it null.
    /// </summary>
    public string? Id { get; init; }

    /// <summary>
    /// When non-null, determines whether the widget is rendered at all.
    /// Returning <c>false</c> removes the widget from layout; returning
    /// <c>true</c> or leaving this null includes it.
    /// </summary>
    public Func<PageState, bool>? Visible { get; init; }

    /// <summary>
    /// When non-null, determines whether the widget accepts user input.
    /// Rendered but greyed-out when the predicate returns <c>false</c>.
    /// </summary>
    public Func<PageState, bool>? Enabled { get; init; }
}
