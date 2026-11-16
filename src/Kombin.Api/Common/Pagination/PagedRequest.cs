namespace Kombin.Api.Common.Pagination;

/// <summary>Page-based paging input. Out-of-range values are clamped instead of rejected.</summary>
internal sealed record PagedRequest(int Page = 1, int PageSize = PagedRequest.DefaultPageSize)
{
    public const int DefaultPageSize = 20;

    public const int MaxPageSize = 100;

    /// <summary>Far beyond any real list; it keeps <see cref="Skip"/> (at most 10,000 x 1,000 here and in the worker listing) far from <see cref="int.MaxValue"/>.</summary>
    public const int MaxPage = 10_000;

    public int EffectivePage => Math.Clamp(Page, 1, MaxPage);

    public int EffectivePageSize => Math.Clamp(PageSize, 1, MaxPageSize);

    public int Skip => (EffectivePage - 1) * EffectivePageSize;
}
