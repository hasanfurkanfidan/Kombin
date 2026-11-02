using Kombin.Api.Common.Results;
using Kombin.Api.Common.Storage;
using Kombin.Api.Features.Wardrobe;
using Kombin.Api.Features.Wardrobe.RecordWear;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Recommendation;

namespace Kombin.Api.Features.Recommendation;

/// <summary>
/// What the user can do with an outfit, shared by the dedicated endpoints (<c>save</c>, <c>worn</c>, <c>DELETE</c>) and the generic
/// <c>feedback</c>. Every action appends an <see cref="OutfitFeedback"/> row; the outfit's status is the latest state.
/// </summary>
internal static class OutfitActions
{
    /// <summary>The caller's outfit, tracked, with its pieces; null for someone else's.</summary>
    public static Task<Outfit?> FindAsync(KombinDbContext db, Guid userId, Guid id, CancellationToken cancellationToken) =>
        db.Outfits.Include(outfit => outfit.Items).SingleOrDefaultAsync(outfit => outfit.Id == id && outfit.UserId == userId, cancellationToken);

    /// <summary>Keeps the outfit (the seed of the social feed). Saving twice keeps the first <c>SavedAt</c>; a worn outfit stays Worn.</summary>
    public static Result Save(KombinDbContext db, Outfit outfit, DateTimeOffset now)
    {
        if (outfit.Status == OutfitStatus.Dismissed)
        {
            return RecommendationErrors.OutfitDismissed;
        }

        outfit.SavedAt ??= now;
        if (outfit.Status == OutfitStatus.Suggested)
        {
            outfit.Status = OutfitStatus.Saved;
        }

        Record(db, outfit, OutfitFeedbackAction.Save, null, now);
        return Result.Success();
    }

    /// <summary>
    /// Marks every wardrobe piece of the outfit worn today (Europe/Istanbul) through the wardrobe's own wear logic, with the outfit on the
    /// wear row. Catalog pieces are not owned and archived items are skipped; an outfit with nothing left to wear is a conflict.
    /// </summary>
    public static async Task<Result> WearAsync(KombinDbContext db, Outfit outfit, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (outfit.Status == OutfitStatus.Dismissed)
        {
            return RecommendationErrors.OutfitDismissed;
        }

        var itemIds = outfit.Items.Select(item => item.WardrobeItemId).OfType<Guid>().ToList();
        var items = await db.WardrobeItems
            .Where(item => itemIds.Contains(item.Id) && item.UserId == outfit.UserId && !item.IsArchived)
            .OrderBy(item => item.Id)
            .ToListAsync(cancellationToken);

        if (items.Count == 0)
        {
            return RecommendationErrors.NothingToWear;
        }

        var today = WearRules.Today(now);
        foreach (var item in items)
        {
            await WearSummaries.RecordAsync(db, item, today, now, outfit.Id, cancellationToken);
        }

        outfit.Status = OutfitStatus.Worn;
        outfit.WornAt = now;
        Record(db, outfit, OutfitFeedbackAction.Worn, null, now);

        return Result.Success();
    }

    /// <summary>Dismissing twice keeps the first time; the latest reason wins. The combination is not suggested again for 30 days.</summary>
    public static void Dismiss(KombinDbContext db, Outfit outfit, DismissReason reason, DateTimeOffset now)
    {
        outfit.Status = OutfitStatus.Dismissed;
        outfit.DismissedAt ??= now;
        outfit.DismissReason = reason;
        Record(db, outfit, OutfitFeedbackAction.Dismiss, reason, now);
    }

    /// <summary>"Another one": the app asked for the next seed instead of this outfit. Only recorded.</summary>
    public static void Shuffle(KombinDbContext db, Outfit outfit, DateTimeOffset now) => Record(db, outfit, OutfitFeedbackAction.Shuffle, null, now);

    public static async Task<OutfitResponse> RespondAsync(KombinDbContext db, IObjectStorage storage, Outfit outfit, CancellationToken cancellationToken) =>
        (await OutfitResponses.BuildAsync(db, storage, [outfit], cancellationToken))[0];

    private static void Record(KombinDbContext db, Outfit outfit, OutfitFeedbackAction action, DismissReason? reason, DateTimeOffset now) =>
        db.OutfitFeedback.Add(new OutfitFeedback { OutfitId = outfit.Id, UserId = outfit.UserId, Action = action, Reason = reason, CreatedAt = now });
}
