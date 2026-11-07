using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Storage;
using Kombin.Api.Common.Time;
using Kombin.Api.Features.Recommendation;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Wardrobe;
using Microsoft.Extensions.Caching.Memory;

namespace Kombin.Api.Features.Wardrobe.GetStats;

/// <summary><c>Slug</c> is the item's own (leaf) category slug; null counts the items without a category.</summary>
internal sealed record CategoryCount(string? Slug, int Count);

internal sealed record ColorCount(ProductColor Color, int Count);

/// <summary>
/// Over the wardrobe (not archived, not failed). An item counts as unworn for 180 days when its last wear, or its creation when it was
/// never worn, is more than 180 days ago. <c>PossibleOutfits</c> is the outfit engine's count of complete, wearable core outfits (top,
/// bottom, shoes) the wardrobe makes, capped at 999 (see <c>OutfitCounter</c>); never null since Task 12. <c>UnmatchedCount</c> is how many
/// wearable pieces pair with nothing else in the wardrobe (<c>companionCount</c> 0 on the grid): the pieces a purchase would unlock.
/// </summary>
internal sealed record WardrobeStatsResponse(
    int ItemCount,
    IReadOnlyList<CategoryCount> ByCategory,
    IReadOnlyList<ColorCount> ByColor,
    int UnwornFor180Days,
    IReadOnlyList<WardrobeItemSummary> MostWorn,
    IReadOnlyList<WardrobeItemSummary> LeastWorn,
    int? PossibleOutfits,
    int UnmatchedCount);

internal static class GetStatsEndpoint
{
    public const int UnwornDays = 180;

    public const int TopCount = 3;

    public static void Map(RouteGroupBuilder group) =>
        group.MapGet("/stats", Handle)
            .WithName("GetWardrobeStats")
            .Produces<WardrobeStatsResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

    private static async Task<IResult> Handle(
        ICurrentUser currentUser, KombinDbContext db, IObjectStorage storage, IClock clock, IMemoryCache cache, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var cutoffDay = WearRules.Today(now).AddDays(-UnwornDays);
        var cutoffInstant = now.AddDays(-UnwornDays);

        var items = db.WardrobeItems.AsNoTracking()
            .VisibleOf(currentUser.UserId)
            .Where(item => !item.IsArchived && item.Status != WardrobeItemStatus.Failed);

        var itemCount = await items.CountAsync(cancellationToken);

        var byCategory = await items
            .GroupBy(item => item.CategoryId)
            .Select(group => new { CategoryId = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);
        var categoryIds = byCategory.Select(row => row.CategoryId).OfType<Guid>().ToList();
        var slugs = await db.Categories.AsNoTracking()
            .Where(category => categoryIds.Contains(category.Id))
            .ToDictionaryAsync(category => category.Id, category => category.Slug, cancellationToken);

        var byColor = await items
            .GroupBy(item => item.PrimaryColor)
            .Select(group => new ColorCount(group.Key, group.Count()))
            .ToListAsync(cancellationToken);

        var unworn = await items.CountAsync(
            item => item.LastWornAt < cutoffDay || (item.LastWornAt == null && item.CreatedAt < cutoffInstant),
            cancellationToken);

        var mostWorn = await items
            .Where(item => item.WearCount > 0)
            .OrderByDescending(item => item.WearCount)
            .ThenByDescending(item => item.LastWornAt)
            .ThenBy(item => item.Id)
            .Take(TopCount)
            .ToListAsync(cancellationToken);

        // Least worn among the ready items: the never-worn first, the longest-unworn among equals.
        var leastWorn = await items
            .Where(item => item.Status == WardrobeItemStatus.Ready)
            .OrderBy(item => item.WearCount)
            .ThenBy(item => item.LastWornAt)
            .ThenBy(item => item.CreatedAt)
            .ThenBy(item => item.Id)
            .Take(TopCount)
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(new WardrobeStatsResponse(
            itemCount,
            [
                .. byCategory
                    .Select(row => new CategoryCount(row.CategoryId is { } id ? slugs.GetValueOrDefault(id) : null, row.Count))
                    .OrderByDescending(row => row.Count)
                    .ThenBy(row => row.Slug, StringComparer.Ordinal),
            ],
            [.. byColor.OrderByDescending(row => row.Count).ThenBy(row => row.Color)],
            unworn,
            await WardrobeItemResponses.SummarizeAsync(db, storage, mostWorn, cancellationToken),
            await WardrobeItemResponses.SummarizeAsync(db, storage, leastWorn, cancellationToken),
            await OutfitCounts.PossibleOutfitsAsync(db, cache, currentUser.UserId, cancellationToken),
            await UnmatchedAsync(db, cache, currentUser.UserId, cancellationToken)));
    }

    /// <summary>
    /// Ready, categorised pieces with no companion (the grid's <c>companionCount</c> 0). Items the engine cannot use (no category, still
    /// processing) are not counted: they are not unmatched, they are unfinished. The companion counts hold every wearable piece (cached
    /// under the wardrobe fingerprint, shared with the grid).
    /// </summary>
    private static async Task<int> UnmatchedAsync(KombinDbContext db, IMemoryCache cache, Guid userId, CancellationToken cancellationToken) =>
        (await OutfitCounts.CompanionCountsAsync(db, cache, userId, cancellationToken)).Count(pair => pair.Value == 0);
}
