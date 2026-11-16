using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Profile;
using Npgsql;

namespace Kombin.Api.Features.Profile;

/// <summary>What an answer does to the size map.</summary>
internal enum SizeEffect
{
    /// <summary>The size map is left as it is.</summary>
    None,

    /// <summary>The named size becomes the user's size for the brand and group: source <see cref="SizeSource.Confirmed"/>, confidence 1.</summary>
    Confirm,

    /// <summary>The stored size loses confidence (<c>ConfirmSizeEndpoint.DoubtedConfidence</c>); nothing else is guessed.</summary>
    Doubt,
}

/// <summary>
/// The rules of "Aldın mı? Beden uydu mu?" (design: <c>docs/design/wardrobe-summary-and-size-memory.md</c>). A question opens
/// <see cref="OpensAfter"/> after the click on the seller and expires <see cref="Lifetime"/> after it; there is one per (user, product), ever.
/// </summary>
internal static class SizePromptRules
{
    public static readonly TimeSpan OpensAfter = TimeSpan.FromDays(3);

    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);

    /// <summary>At most this many open questions are shown at once; the rest wait.</summary>
    public const int MaxShown = 3;

    public static DateTimeOffset ExpiresAt(DateTimeOffset clickedAt) => clickedAt + Lifetime;

    public static bool IsExpired(DateTimeOffset clickedAt, DateTimeOffset now) => now >= ExpiresAt(clickedAt);

    /// <summary>The accessories group is the size map's catch-all (bags, belts): there is no size to remember, so nobody is asked.</summary>
    public static bool HasSize(SizeGroup group) => group != SizeGroup.Accessories;

    /// <summary>
    /// A confirmed fit is the only answer that sets a size, and it replaces what the user typed (it is the stronger evidence). "Too small" and
    /// "too large" never pick a new size: they doubt the stored one, and only when it is the size that was bought (or the answer names none). A
    /// different size, or no stored size, changes nothing.
    /// </summary>
    public static SizeEffect EffectOf(SizePromptAnswer answer, string? answeredSize, string? storedSize) => answer switch
    {
        SizePromptAnswer.BoughtFits => SizeEffect.Confirm,
        SizePromptAnswer.BoughtTooSmall or SizePromptAnswer.BoughtTooLarge
            when storedSize is not null && (answeredSize is null || answeredSize == storedSize) => SizeEffect.Doubt,
        _ => SizeEffect.None,
    };
}

internal static class SizePrompts
{
    /// <summary>
    /// Clicks looked at per request, the newest first. Only clicks on products with a size count towards it (the filter is in the query, before the
    /// limit), so clicks on bags and belts, which never become a question, cannot fill the window and push the others out.
    /// </summary>
    private const int MaxClicksRead = 200;

    /// <summary>
    /// Creates the questions the user's clicks have earned and that do not exist yet: the first click per product among those at least
    /// <see cref="SizePromptRules.OpensAfter"/> old and not yet expired, products with a size only. Idempotent; the unique index makes two
    /// parallel requests safe (the loser reads again and finds the winner's rows). The click itself is never touched.
    /// </summary>
    public static async Task MaterializeAsync(KombinDbContext db, Guid userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var openedBefore = now - SizePromptRules.OpensAfter;
        var notExpiredSince = now - SizePromptRules.Lifetime;

        var hasSize = SizeGroups.HasSizeGroup();

        for (var attempt = 1; ; attempt++)
        {
            var clicks = await db.Clicks.AsNoTracking()
                .Where(click => click.UserId == userId && click.ClickedAt <= openedBefore && click.ClickedAt > notExpiredSince)
                .Where(click => db.Products.Where(hasSize).Any(product => product.Id == click.ProductId))
                .Where(click => !db.SizePrompts.Any(prompt => prompt.UserId == userId && prompt.ProductId == click.ProductId))
                .OrderByDescending(click => click.ClickedAt)
                .Take(MaxClicksRead)
                .Select(click => new
                {
                    click.Id,
                    click.ProductId,
                    click.SellerId,
                    click.OfferId,
                    click.ClickedAt,
                    CategorySlug = db.Products.Where(product => product.Id == click.ProductId).Select(product => product.Category.Slug).First(),
                })
                .ToListAsync(cancellationToken);

            // The group is judged by the rules again here (the query's filter is its SQL twin; a test pins that they agree).
            var firstPerProduct = clicks
                .GroupBy(click => click.ProductId)
                .Select(group => group.OrderBy(click => click.ClickedAt).ThenBy(click => click.Id).First())
                .Where(click => SizePromptRules.HasSize(SizeGroups.ForCategorySlug(click.CategorySlug)))
                .ToList();

            if (firstPerProduct.Count == 0)
            {
                return;
            }

            db.SizePrompts.AddRange(firstPerProduct.Select(click => new SizePrompt
            {
                UserId = userId,
                ProductId = click.ProductId,
                SellerId = click.SellerId,
                OfferId = click.OfferId,
                ClickId = click.Id,
                ClickedAt = click.ClickedAt,
            }));

            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (DbUpdateException exception) when (attempt == 1 && exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                db.ChangeTracker.Clear();
            }
        }
    }
}
