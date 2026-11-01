namespace Kombin.Api.Common.Pagination;

/// <summary>Page-based paging input. Out-of-range values are clamped instead of rejected.</summary>
internal sealed record PagedRequest(int Page = 1, int PageSize = PagedRequest.DefaultPageSize)
{
    public const int DefaultPageSize = 20;

    public const int MaxPageSize = 100;

    public int EffectivePage => Math.Max(Page, 1);

    public int EffectivePageSize => Math.Clamp(PageSize, 1, MaxPageSize);

    public int Skip => (EffectivePage - 1) * EffectivePageSize;
}
