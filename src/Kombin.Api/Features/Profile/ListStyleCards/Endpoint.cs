using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Results;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Profile;

namespace Kombin.Api.Features.Profile.ListStyleCards;

internal sealed record StyleCardItem(Guid Id, string Title, string ImageUrl, Audience Audience, StyleFamily Family);

/// <summary><c>Remaining</c> counts the unswiped cards that did not fit into this response.</summary>
internal sealed record StyleCardsResponse(IReadOnlyList<StyleCardItem> Items, int Remaining);

internal static class ListStyleCardsEndpoint
{
    public const int DefaultLimit = 20;

    public const int MaxLimit = 50;

    public static void Map(RouteGroupBuilder group) =>
        group.MapGet("/{id:guid}/style-cards", Handle)
            .WithName("ListStyleCards")
            .Produces<StyleCardsResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

    private static async Task<IResult> Handle(Guid id, int? limit, ICurrentUser currentUser, KombinDbContext db, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId;

        if (!await db.Personas.OwnedBy(userId).AnyAsync(persona => persona.Id == id, cancellationToken))
        {
            return ProfileErrors.PersonaNotFound.ToHttpResult();
        }

        // A user without a profile row yet has the default audience.
        var audience = await db.UserProfiles
            .AsNoTracking()
            .Where(profile => profile.UserId == userId)
            .Select(profile => (Audience?)profile.Audience)
            .SingleOrDefaultAsync(cancellationToken) ?? Audience.Unisex;

        var swiped = db.StyleSwipes.Where(swipe => swipe.PersonaId == id).Select(swipe => swipe.StyleCardId);

        // The whole deck is a few dozen rows, so it is filtered and interleaved in memory.
        var unswiped = await db.StyleCards
            .AsNoTracking()
            .Where(card => card.IsActive && !swiped.Contains(card.Id))
            .ToListAsync(cancellationToken);

        var feed = StyleCardFeed.Interleave(unswiped.Where(card => StyleCardFeed.IsVisibleTo(audience, card.Audience)));
        var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);

        var items = feed
            .Take(take)
            .Select(card => new StyleCardItem(card.Id, card.Title, card.ImageUrl, card.Audience, card.Family))
            .ToList();

        return Results.Ok(new StyleCardsResponse(items, feed.Count - items.Count));
    }
}
