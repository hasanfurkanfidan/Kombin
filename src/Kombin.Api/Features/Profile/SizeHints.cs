using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Profile;

namespace Kombin.Api.Features.Profile;

/// <summary>
/// Which size to buy. <c>Size</c>, <c>Source</c> and <c>Confidence</c> are the user's own entry for the product's brand and group, when
/// there is one. Without it <c>Suggestion</c> may carry a guess from the user's sizes at other brands (not stored).
/// </summary>
internal sealed record SizeHintResponse(SizeGroup Group, string? Size, SizeSource? Source, decimal? Confidence, SizeSuggestion? Suggestion);

internal static class SizeHints
{
    public static async Task<SizeHintResponse> BuildAsync(
        KombinDbContext db, Guid userId, Guid brandId, string categorySlug, CancellationToken cancellationToken)
    {
        var group = SizeGroups.ForCategorySlug(categorySlug);

        var entries = await db.SizeEntries
            .AsNoTracking()
            .Where(entry => entry.UserId == userId && entry.Group == group)
            .Select(entry => new { entry.BrandId, entry.Size, entry.Source, entry.Confidence, entry.UpdatedAt })
            .ToListAsync(cancellationToken);

        var own = entries.SingleOrDefault(entry => entry.BrandId == brandId);

        if (own is not null)
        {
            return new SizeHintResponse(group, own.Size, own.Source, own.Confidence, null);
        }

        var votes = entries.Select(entry => new SizeVote(entry.Size, entry.UpdatedAt)).ToList();

        return new SizeHintResponse(group, null, null, null, SizeInference.Suggest(votes));
    }
}
