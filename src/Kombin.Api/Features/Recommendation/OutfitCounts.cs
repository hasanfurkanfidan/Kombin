using System.Globalization;
using Kombin.Api.Features.Profile;
using Kombin.Api.Features.Wardrobe;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Catalog;
using Microsoft.Extensions.Caching.Memory;

namespace Kombin.Api.Features.Recommendation;

/// <summary>The engine's numbers for the Wardrobe feature: <c>possibleOutfits</c> on the stats and <c>unlocksEstimate</c> on the gaps.</summary>
internal static class OutfitCounts
{
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromDays(1);

    /// <summary>
    /// <see cref="OutfitCounter.Count"/> over the user's wardrobe, cached in memory under a fingerprint of the wardrobe (item count and
    /// latest <c>UpdatedAt</c>): adding, editing, archiving or wearing a piece changes the fingerprint, which is the invalidation.
    /// </summary>
    public static async Task<int> PossibleOutfitsAsync(KombinDbContext db, IMemoryCache cache, Guid userId, CancellationToken cancellationToken)
    {
        var fingerprint = await db.WardrobeItems.AsNoTracking()
            .Where(item => item.UserId == userId)
            .GroupBy(_ => 1)
            .Select(group => new { Count = group.Count(), Latest = group.Max(item => item.UpdatedAt) })
            .SingleOrDefaultAsync(cancellationToken);

        if (fingerprint is null)
        {
            return 0;
        }

        var key = string.Create(CultureInfo.InvariantCulture, $"possible-outfits:{userId:N}:{fingerprint.Count}:{fingerprint.Latest.UtcTicks}");
        if (cache.TryGetValue(key, out int count))
        {
            return count;
        }

        count = OutfitCounter.Count(await OutfitSources.LoadWardrobeAsync(db, userId, scoring: null, cancellationToken));
        cache.Set(key, count, CacheLifetime);

        return count;
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

    /// <summary>The gap analysis' view of the same pieces (it groups by size group, so a blazer counts as a top there).</summary>
    public static GapPiece ToGapPiece(OutfitPiece piece)
    {
        ArgumentNullException.ThrowIfNull(piece);

        return new GapPiece(SizeGroups.ForCategorySlug(piece.CategorySlug), piece.Color, piece.Formality);
    }
}
