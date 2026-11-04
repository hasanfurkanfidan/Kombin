using System.Globalization;
using Kombin.Api.Features.Profile;
using Kombin.Api.Features.Wardrobe;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Catalog;
using Microsoft.Extensions.Caching.Memory;

namespace Kombin.Api.Features.Recommendation;

/// <summary>
/// The engine's numbers for the Wardrobe feature: <c>possibleOutfits</c> on the stats, <c>unlocksEstimate</c> on the gaps, and
/// <c>outfitCount</c> and <c>companionCount</c> on the pairings and the grid.
/// </summary>
internal static class OutfitCounts
{
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromDays(1);

    /// <summary><see cref="OutfitCounter.Count"/> over the user's wardrobe.</summary>
    public static Task<int> PossibleOutfitsAsync(KombinDbContext db, IMemoryCache cache, Guid userId, CancellationToken cancellationToken) =>
        CachedAsync(
            db,
            cache,
            userId,
            "possible-outfits",
            0,
            async () => OutfitCounter.Count(await OutfitSources.LoadWardrobeAsync(db, userId, scoring: null, cancellationToken)),
            cancellationToken);

    /// <summary><see cref="OutfitPairs.CountCompanions"/> over the user's wardrobe: one pass serves the whole grid. Items outside the wardrobe are absent.</summary>
    public static Task<IReadOnlyDictionary<Guid, int>> CompanionCountsAsync(KombinDbContext db, IMemoryCache cache, Guid userId, CancellationToken cancellationToken) =>
        CachedAsync<IReadOnlyDictionary<Guid, int>>(
            db,
            cache,
            userId,
            "companion-counts",
            new Dictionary<Guid, int>(),
            async () => OutfitPairs.CountCompanions(await OutfitSources.LoadWardrobeAsync(db, userId, scoring: null, cancellationToken)),
            cancellationToken);

    /// <summary>How many of the outfits behind <c>possibleOutfits</c> wear <paramref name="item"/>, a piece of <paramref name="wardrobe"/>.</summary>
    public static Task<int> ItemOutfitsAsync(
        KombinDbContext db, IMemoryCache cache, Guid userId, OutfitPiece item, IReadOnlyList<OutfitPiece> wardrobe, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);

        return CachedAsync(
            db,
            cache,
            userId,
            string.Create(CultureInfo.InvariantCulture, $"item-outfits:{item.Id:N}"),
            0,
            () => Task.FromResult(OutfitCounter.CountIncluding(wardrobe, [item])),
            cancellationToken);
    }

    /// <summary>
    /// The gap's <c>unlocksEstimate</c> by the engine: the outfits a plain (solid, regular-fit, all-season) piece of the gap's category,
    /// colour and formality would make possible with <paramref name="wardrobe"/>.
    /// </summary>
    public static int Unlocks(IReadOnlyList<OutfitPiece> wardrobe, WardrobeGap gap)
    {
        ArgumentNullException.ThrowIfNull(gap);

        var hypothetical = new OutfitPiece(
            PieceSource.Catalog,
            Guid.Empty,
            OutfitSlots.ForCategorySlug(gap.CategorySlug),
            gap.CategorySlug,
            gap.Color,
            ProductPattern.Solid,
            ProductFit.Regular,
            gap.Formality,
            Season.AllYear);

        return OutfitCounter.CountWith(wardrobe, hypothetical);
    }

    /// <summary>
    /// A number derived from the wardrobe, cached in memory under a fingerprint of it (item count and latest <c>UpdatedAt</c>): adding,
    /// editing, archiving or wearing a piece changes the fingerprint, which is the invalidation. <paramref name="empty"/> answers for a
    /// user without items.
    /// </summary>
    private static async Task<T> CachedAsync<T>(
        KombinDbContext db, IMemoryCache cache, Guid userId, string name, T empty, Func<Task<T>> compute, CancellationToken cancellationToken)
        where T : notnull
    {
        var fingerprint = await db.WardrobeItems.AsNoTracking()
            .Where(item => item.UserId == userId)
            .GroupBy(_ => 1)
            .Select(group => new { Count = group.Count(), Latest = group.Max(item => item.UpdatedAt) })
            .SingleOrDefaultAsync(cancellationToken);

        if (fingerprint is null)
        {
            return empty;
        }

        var key = string.Create(CultureInfo.InvariantCulture, $"{name}:{userId:N}:{fingerprint.Count}:{fingerprint.Latest.UtcTicks}");
        if (cache.TryGetValue(key, out T? cached) && cached is not null)
        {
            return cached;
        }

        var value = await compute();
        cache.Set(key, value, CacheLifetime);

        return value;
    }

    /// <summary>The gap analysis' view of the same pieces (it groups by size group, so a blazer counts as a top there).</summary>
    public static GapPiece ToGapPiece(OutfitPiece piece)
    {
        ArgumentNullException.ThrowIfNull(piece);

        return new GapPiece(SizeGroups.ForCategorySlug(piece.CategorySlug), piece.Color, piece.Formality);
    }
}
