using Kombin.Api.Common.Results;
using Kombin.Api.Common.Validation;
using Kombin.Api.Persistence.Entities.League;

namespace Kombin.Api.Features.League.Internal.Review;

/// <summary><c>Decision</c>: <c>Restore</c> (clear the reports, show a held-back result) or <c>Remove</c> (take the outfit out for good), any casing.</summary>
internal sealed record ReviewEntryRequest(string? Decision);

/// <summary>The entry's status after the review, and whether a closed day's result changed with it.</summary>
internal sealed record ReviewEntryResponse(Guid EntryId, LeagueEntryStatus Status, bool ResultChanged);

internal static class ReviewEntryEndpoint
{
    /// <summary>The audit log's actor for a decision made with the internal key (the admin panel will pass the admin's e-mail).</summary>
    public const string InternalActor = "internal-key";

    public static void Map(RouteGroupBuilder group) =>
        group.MapPost("/entries/{entryId:guid}/review", Handle)
            .WithName("ReviewLeagueEntry")
            .WithValidation<ReviewEntryRequest>()
            .Produces<ReviewEntryResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

    /// <summary>
    /// Restores or removes a reported outfit through <see cref="LeagueEntryReviewer"/> (its rules, the audit log row). Errors: 400 validation;
    /// 404 <c>League.EntryNotFound</c>; 409 <c>League.NotReviewable</c> (restoring a removed outfit).
    /// </summary>
    private static async Task<IResult> Handle(Guid entryId, ReviewEntryRequest request, LeagueEntryReviewer reviewer, CancellationToken cancellationToken)
    {
        var decision = LeagueChoices.TryParse<LeagueReviewDecision>(request.Decision, out var parsed)
            ? parsed
            : throw new ArgumentException("The validator lets only Restore and Remove through.", nameof(request));
        var result = await reviewer.ReviewAsync(entryId, decision, InternalActor, cancellationToken);

        return result.Outcome switch
        {
            LeagueReviewOutcome.NotFound => LeagueErrors.EntryNotFound.ToHttpResult(),
            LeagueReviewOutcome.NotReviewable => LeagueErrors.NotReviewable.ToHttpResult(),
            _ => TypedResults.Ok(new ReviewEntryResponse(result.EntryId, result.Status!.Value, result.ResultChanged)),
        };
    }
}
