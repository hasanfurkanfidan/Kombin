using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Time;
using Kombin.Api.Features.Catalog;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Profile;

namespace Kombin.Api.Features.Profile.ListSizePrompts;

internal sealed record SizePromptProduct(Guid Id, string Name, string? ImageUrl);

/// <summary>The size map's entry for the product's brand and group, so the app can pre-fill the answer. Absent when the user has none.</summary>
internal sealed record KnownSize(string Size, SizeSource Source, decimal Confidence);

/// <summary>
/// One open question. <c>Group</c> is the size group the answer will be stored under; <c>AvailableSizes</c> are the sizes the clicked offer
/// listed (possibly none); <c>ExpiresAt</c> is when the question goes away by itself.
/// </summary>
internal sealed record SizePromptResponse(
    Guid Id,
    SizePromptProduct Product,
    BrandRef Brand,
    SellerRef Seller,
    SizeGroup Group,
    KnownSize? KnownSize,
    IReadOnlyList<string> AvailableSizes,
    DateTimeOffset ClickedAt,
    DateTimeOffset ExpiresAt);

internal sealed record SizePromptsResponse(IReadOnlyList<SizePromptResponse> Prompts);

internal static class ListSizePromptsEndpoint
{
    /// <summary>
    /// <c>GET /me/size-prompts</c>: the open "Aldın mı? Beden uydu mu?" questions, newest click first, at most <see cref="SizePromptRules.MaxShown"/>.
    /// Creates the questions the user's clicks earned first (<see cref="SizePrompts.MaterializeAsync"/>), the way <c>GET /me/profile</c> creates
    /// the profile. Empty while the user has switched the questions off (<c>sizePromptsEnabled</c> on the profile); nothing is created then.
    /// </summary>
    public static void Map(RouteGroupBuilder group) =>
        group.MapGet("/", Handle)
            .WithName("ListSizePrompts")
            .Produces<SizePromptsResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

    private static async Task<IResult> Handle(ICurrentUser currentUser, KombinDbContext db, IClock clock, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId;
        var now = clock.UtcNow;

        // A user without a profile row has never switched anything off.
        var enabled = await db.UserProfiles.AsNoTracking()
            .Where(profile => profile.UserId == userId)
            .Select(profile => (bool?)profile.SizePromptsEnabled)
            .SingleOrDefaultAsync(cancellationToken) ?? true;

        if (!enabled)
        {
            return TypedResults.Ok(new SizePromptsResponse([]));
        }

        await SizePrompts.MaterializeAsync(db, userId, now, cancellationToken);

        var openedBefore = now - SizePromptRules.OpensAfter;
        var notExpiredSince = now - SizePromptRules.Lifetime;

        var rows = await (
            from prompt in db.SizePrompts.AsNoTracking()
            where prompt.UserId == userId && prompt.Answer == null && prompt.ClickedAt <= openedBefore && prompt.ClickedAt > notExpiredSince
            orderby prompt.ClickedAt descending, prompt.Id descending
            join product in db.Products on prompt.ProductId equals product.Id
            join seller in db.Sellers on prompt.SellerId equals seller.Id
            select new
            {
                prompt.Id,
                prompt.ClickedAt,
                ProductId = product.Id,
                ProductName = product.Name,
                product.PrimaryImageUrl,
                BrandId = product.Brand.Id,
                BrandName = product.Brand.Name,
                BrandSlug = product.Brand.Slug,
                CategorySlug = product.Category.Slug,
                SellerName = seller.Name,
                SellerSlug = seller.Slug,
                Sizes = db.Offers.Where(offer => offer.Id == prompt.OfferId).Select(offer => offer.AvailableSizes).FirstOrDefault(),
            })
            .Take(SizePromptRules.MaxShown)
            .ToListAsync(cancellationToken);

        var brandIds = rows.Select(row => row.BrandId).Distinct().ToList();
        var known = await db.SizeEntries.AsNoTracking()
            .Where(entry => entry.UserId == userId && brandIds.Contains(entry.BrandId))
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(new SizePromptsResponse(
        [
            .. rows.Select(row =>
            {
                var group = SizeGroups.ForCategorySlug(row.CategorySlug);
                var entry = known.FirstOrDefault(candidate => candidate.BrandId == row.BrandId && candidate.Group == group);

                return new SizePromptResponse(
                    row.Id,
                    new SizePromptProduct(row.ProductId, row.ProductName, row.PrimaryImageUrl),
                    new BrandRef(row.BrandId, row.BrandName, row.BrandSlug),
                    new SellerRef(row.SellerName, row.SellerSlug),
                    group,
                    entry is null ? null : new KnownSize(entry.Size, entry.Source, entry.Confidence),
                    row.Sizes ?? [],
                    row.ClickedAt,
                    SizePromptRules.ExpiresAt(row.ClickedAt));
            }),
        ]));
    }
}
