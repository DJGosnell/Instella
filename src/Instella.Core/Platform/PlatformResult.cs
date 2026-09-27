namespace Instella.Core.Platform;

/// <summary>
/// The outcome of an <see cref="IPlatformServices"/> operation: success, or failure with the
/// reason. A record, so fields can be added later without breaking callers.
/// </summary>
/// <param name="Success">True when the operation completed (or had nothing to do).</param>
/// <param name="Error">Why the operation failed; null on success.</param>
public sealed record PlatformResult(bool Success, string? Error = null)
{
    /// <summary>A successful result.</summary>
    public static PlatformResult Ok { get; } = new(true);

    /// <summary>A failed result with the reason.</summary>
    public static PlatformResult Fail(string error) => new(false, error);
}
