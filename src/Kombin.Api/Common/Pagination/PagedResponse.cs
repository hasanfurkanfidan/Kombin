namespace Kombin.Api.Common.Pagination;

internal sealed record PagedResponse<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount)
{
    public int TotalPages => (int)Math.Ceiling(TotalCount / (double)PageSize);

    public static PagedResponse<T> Create(IReadOnlyList<T> items, PagedRequest request, int totalCount) =>
        new(items, request.EffectivePage, request.EffectivePageSize, totalCount);
}
