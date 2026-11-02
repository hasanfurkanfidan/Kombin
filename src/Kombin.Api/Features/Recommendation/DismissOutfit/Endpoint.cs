using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Results;
using Kombin.Api.Common.Time;
using Kombin.Api.Common.Validation;
using Kombin.Api.Features.Catalog;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Recommendation;

namespace Kombin.Api.Features.Recommendation.DismissOutfit;

/// <summary><c>Reason</c>: DontLikeColors, TooFormal, TooCasual, WrongWeather, DontOwnAnymore or Other (default).</summary>
internal sealed record DismissOutfitRequest(Guid Id, string? Reason);

internal static class DismissOutfitEndpoint
{
    public static void Map(RouteGroupBuilder group) =>
        group.MapDelete("/{id:guid}", Handle)
            .WithName("DismissOutfit")
            .WithValidation<DismissOutfitRequest>()
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

    /// <summary>Dismisses the outfit (the row stays, for feedback); the same pieces are not suggested again for 30 days.</summary>
    private static async Task<IResult> Handle(
        [AsParameters] DismissOutfitRequest request, ICurrentUser currentUser, KombinDbContext db, IClock clock, CancellationToken cancellationToken)
    {
        var outfit = await OutfitActions.FindAsync(db, currentUser.UserId, request.Id, cancellationToken);
        if (outfit is null)
        {
            return RecommendationErrors.OutfitNotFound.ToHttpResult();
        }

        var reason = EnumNames.TryParse<DismissReason>(request.Reason, out var parsed) ? parsed : DismissReason.Other;
        OutfitActions.Dismiss(db, outfit, reason, clock.UtcNow);
        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.NoContent();
    }
}
