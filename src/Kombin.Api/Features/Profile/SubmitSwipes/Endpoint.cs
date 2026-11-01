using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Results;
using Kombin.Api.Common.Time;
using Kombin.Api.Common.Validation;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Profile;

namespace Kombin.Api.Features.Profile.SubmitSwipes;

internal sealed record SwipeItem(Guid StyleCardId, string Decision);

internal sealed record SubmitSwipesRequest(IReadOnlyList<SwipeItem> Swipes);

/// <summary><c>OnboardingCompleted</c> turns true once any persona has reached <see cref="SubmitSwipesEndpoint.OnboardingLikes"/> likes.</summary>
internal sealed record SubmitSwipesResponse(StyleSummary StyleSummary, bool OnboardingCompleted);

internal static class SubmitSwipesEndpoint
{
    public const int MaxBatchSize = 50;

    public const int OnboardingLikes = 8;

    public static void Map(RouteGroupBuilder group) =>
        group.MapPost("/{id:guid}/swipes", Handle)
            .WithName("SubmitSwipes")
            .WithValidation<SubmitSwipesRequest>()
            .Produces<SubmitSwipesResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

    private static async Task<IResult> Handle(
        Guid id, SubmitSwipesRequest request, ICurrentUser currentUser, KombinDbContext db, IClock clock, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId;
        var userProfile = await UserProfiles.GetOrCreateAsync(db, userId, cancellationToken);

        var persona = await db.Personas.AsNoTracking().OwnedBy(userId).SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (persona is null)
        {
            return ProfileErrors.PersonaNotFound.ToHttpResult();
        }

        // The same card twice in one batch: the last decision wins.
        var decisions = request.Swipes
            .GroupBy(swipe => swipe.StyleCardId)
            .ToDictionary(group => group.Key, group => Enum.Parse<SwipeDecision>(group.Last().Decision, ignoreCase: true));

        // The deck is a few dozen rows; loading all of it covers both the existence check and the recompute below.
        var cards = await db.StyleCards.AsNoTracking().ToDictionaryAsync(card => card.Id, cancellationToken);

        if (decisions.Keys.Any(cardId => !cards.ContainsKey(cardId)))
        {
            return ProfileErrors.StyleCardNotFound.ToHttpResult();
        }

        var swipes = await db.StyleSwipes.Where(swipe => swipe.PersonaId == id).ToListAsync(cancellationToken);
        var now = clock.UtcNow;

        foreach (var (cardId, decision) in decisions)
        {
            var existing = swipes.Find(swipe => swipe.StyleCardId == cardId);

            if (existing is not null)
            {
                existing.Decision = decision;
                continue;
            }

            var created = new StyleSwipe { PersonaId = id, StyleCardId = cardId, Decision = decision, CreatedAt = now };
            db.StyleSwipes.Add(created);
            swipes.Add(created);
        }

        var data = StyleProfileBuilder.Build(swipes.Select(swipe => new StyleSwipeSample(swipe.Decision, cards[swipe.StyleCardId])), persona);

        var styleProfile = await db.PersonaStyleProfiles.SingleOrDefaultAsync(profile => profile.PersonaId == id, cancellationToken);
        if (styleProfile is null)
        {
            styleProfile = new PersonaStyleProfile { PersonaId = id };
            db.PersonaStyleProfiles.Add(styleProfile);
        }

        StyleProfiles.Apply(styleProfile, data, now);

        if (userProfile.OnboardingCompletedAt is null && data.LikeCount >= OnboardingLikes)
        {
            userProfile.OnboardingCompletedAt = now;
        }

        await db.SaveChangesAsync(cancellationToken);

        return Results.Ok(new SubmitSwipesResponse(StyleSummary.From(styleProfile), userProfile.OnboardingCompletedAt is not null));
    }
}
