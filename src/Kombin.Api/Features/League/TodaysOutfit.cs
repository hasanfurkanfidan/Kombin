using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Recommendation;

namespace Kombin.Api.Features.League;

/// <summary>
/// Which outfit is the player's outfit of the day (design note section 5): the rules' "o gün giydiği kombin", i.e. the outfit marked worn
/// ("Bunu giyiyorum" / "Bugün giydim", both <c>POST /me/outfits/{id}/worn</c>) with the latest <c>worn_at</c> inside the Istanbul day and before
/// the close, any kind. Saving never qualifies, a wear after the close belongs to no day's league, and an outfit dismissed after it was worn no
/// longer qualifies (it is no longer <c>Worn</c>). Reads Recommendation's entities; calls none of its code.
/// </summary>
internal static class TodaysOutfit
{
    public static Task<Outfit?> ResolveAsync(KombinDbContext db, Guid userId, DateOnly day, TimeOnly closesAt, CancellationToken cancellationToken)
    {
        var start = LeagueDays.Start(day);
        var end = LeagueDays.ClosesAt(day, closesAt);

        return db.Outfits.AsNoTracking()
            .Include(outfit => outfit.Items)
            .Where(outfit => outfit.UserId == userId && outfit.Status == OutfitStatus.Worn && outfit.WornAt >= start && outfit.WornAt < end)
            .OrderByDescending(outfit => outfit.WornAt)
            .ThenByDescending(outfit => outfit.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
