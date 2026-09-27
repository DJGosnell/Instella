namespace Instella.Server.Models;

/// <summary>
/// Query parameters for paginated, filtered, sorted package retrieval.
/// </summary>
public class PackageQueryParams
{
    /// <summary>1-based page number. Default: 1.</summary>
    public int Page { get; set; } = 1;

    /// <summary>Items per page. Default: 40. Valid values: 20, 40, 100.</summary>
    public int PageSize { get; set; } = 40;

    /// <summary>Optional search term to filter by DisplayName (case-insensitive contains).</summary>
    public string? SearchTerm { get; set; }

    /// <summary>Column to sort by. Valid values: "Name", "Released". Default: "Name".</summary>
    public string SortColumn { get; set; } = "Name";

    /// <summary>Sort direction. True = ascending, False = descending. Default: true.</summary>
    public bool SortAscending { get; set; } = true;

    /// <summary>Validates and normalizes the query parameters.</summary>
    public void Normalize()
    {
        if (Page < 1) Page = 1;
        if (PageSize is not (20 or 40 or 100)) PageSize = 40;
        if (SortColumn is not ("Name" or "Released")) SortColumn = "Name";
        SearchTerm = SearchTerm?.Trim();
        if (string.IsNullOrEmpty(SearchTerm)) SearchTerm = null;
    }
}
