using System.ComponentModel.DataAnnotations;

namespace DocHub.Api.Common;

/// <summary>Paging convention for list endpoints: <c>?page=1&amp;pageSize=50</c> (both optional).</summary>
public sealed record PageRequest(
    [property: Range(1, int.MaxValue)] int Page = 1,
    [property: Range(1, PageRequest.MaxPageSize)] int PageSize = PageRequest.DefaultPageSize)
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;

    public int Skip => (Page - 1) * PageSize;
}

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);
