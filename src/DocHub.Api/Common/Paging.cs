using System.ComponentModel.DataAnnotations;

namespace DocHub.Api.Common;

/// <summary>Paging convention for list endpoints: <c>?page=1&amp;pageSize=50</c> (both optional).</summary>
public sealed record PageRequest(
    [property: Range(1, int.MaxValue)] int Page = 1,
    [property: Range(1, PageRequest.MaxPageSize)] int PageSize = PageRequest.DefaultPageSize)
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;

    /// <summary>Rows to skip; clamped so a huge page number gives an empty page instead of an overflow.</summary>
    public int Skip => (int)Math.Min(((long)Page - 1) * PageSize, int.MaxValue);
}

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);
