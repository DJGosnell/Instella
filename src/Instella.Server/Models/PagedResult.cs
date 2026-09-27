namespace Instella.Server.Models;

/// <summary>
/// Generic wrapper for paginated query results.
/// </summary>
/// <typeparam name="T">Type of items in the result set.</typeparam>
public class PagedResult<T>
{
    /// <summary>Items for the current page.</summary>
    public List<T> Items { get; set; } = [];

    /// <summary>Total count of items matching the query (before pagination).</summary>
    public int TotalCount { get; set; }

    /// <summary>Current page number (1-based).</summary>
    public int Page { get; set; }

    /// <summary>Number of items per page.</summary>
    public int PageSize { get; set; }

    /// <summary>Calculated total number of pages.</summary>
    public int TotalPages => PageSize > 0 ? (int)Math.Ceiling(TotalCount / (double)PageSize) : 0;

    /// <summary>Whether there is a previous page.</summary>
    public bool HasPreviousPage => Page > 1;

    /// <summary>Whether there is a next page.</summary>
    public bool HasNextPage => Page < TotalPages;
}
