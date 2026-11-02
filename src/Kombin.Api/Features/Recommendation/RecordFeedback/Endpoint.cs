using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Results;
using Kombin.Api.Common.Storage;
using Kombin.Api.Common.Time;
using Kombin.Api.Common.Validation;
using Kombin.Api.Features.Catalog;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Recommendation;

namespace Kombin.Api.Features.Recommendation.RecordFeedback;

/// <summary>
/// <c>Action</c>: Save, Dismiss, Worn or Shuffle. Save, Dismiss and Worn do what their own endpoints do; Shuffle ("another one") is only
/// recorded. <c>Reason</c> is for Dismiss only (default Other).
/// </summary>
internal sealed record OutfitFeedbackRequest(string Action, string? Reason);

internal static class RecordFeedbackEndpoint
{
    public static void Map(RouteGroupBuilder group) =>
        group.MapPost("/{id:guid}/feedback", Handle)
            .WithName("RecordOutfitFeedback")
            .WithValidation<OutfitFeedbackRequest>()
            .Produces<OutfitResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

    private static async Task<IResult> Handle(
        Guid id,
        OutfitFeedbackRequest request,
        ICurrentUser currentUser,
        KombinDbContext db,
        IObjectStorage storage,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var outfit = await OutfitActions.FindAsync(db, currentUser.UserId, id, cancellationToken);
        if (outfit is null)
        {
            return RecommendationErrors.OutfitNotFound.ToHttpResult();
        }

        var now = clock.UtcNow;
        var action = Enum.Parse<OutfitFeedbackAction>(request.Action, ignoreCase: true);
        var result = action switch
        {
            OutfitFeedbackAction.Save => OutfitActions.Save(db, outfit, now),
            OutfitFeedbackAction.Worn => await OutfitActions.WearAsync(db, outfit, now, cancellationToken),
            OutfitFeedbackAction.Dismiss => Dismiss(db, outfit, request.Reason, now),
            _ => Shuffle(db, outfit, now),
        };

        if (result.IsFailure)
        {
            return result.ToHttpResult();
        }

        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.Ok(await OutfitActions.RespondAsync(db, storage, outfit, cancellationToken));
    }

    private static Result Dismiss(KombinDbContext db, Outfit outfit, string? reason, DateTimeOffset now)
    {
        OutfitActions.Dismiss(db, outfit, EnumNames.TryParse<DismissReason>(reason, out var parsed) ? parsed : DismissReason.Other, now);
        return Result.Success();
    }

    private static Result Shuffle(KombinDbContext db, Outfit outfit, DateTimeOffset now)
    {
        OutfitActions.Shuffle(db, outfit, now);
        return Result.Success();
    }
}
