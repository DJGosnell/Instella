namespace Instella.Installer.Runtime.Builders;

/// <summary>
/// Outcome of a page-level validation pass. Returned by a
/// <see cref="PageBuilder.OnValidate"/> delegate; a failing result blocks the
/// wizard's <c>Continue</c> action and surfaces <see cref="Error"/> to the
/// user.
/// </summary>
/// <param name="IsValid">True when the page may be left.</param>
/// <param name="Error">What the user must fix; null when valid.</param>
public readonly record struct ValidationResult(bool IsValid, string? Error)
{
    /// <summary>The page is valid.</summary>
    public static ValidationResult Ok { get; } = new(true, null);

    /// <summary>The page is invalid; <paramref name="message"/> is shown to the user.</summary>
    public static ValidationResult Fail(string message) => new(false, message);
}
