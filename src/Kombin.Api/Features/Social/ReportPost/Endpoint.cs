using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Results;
using Kombin.Api.Common.Time;
using Kombin.Api.Common.Validation;
using Kombin.Api.Features.Catalog;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Social;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Kombin.Api.Features.Social.ReportPost;

/// <summary><c>Reason</c>: Nudity, Violence, Hate, Harassment, Spam, Scam or Other.</summary>
internal sealed record ReportPostRequest(string? Reason);

internal sealed record ReportPostResponse(bool Reported);

internal static class ReportPostEndpoint
{
    public static void Map(RouteGroupBuilder group) =>
        group.MapPost("/{id:guid}/report", Handle)
            .WithName("ReportPost")
            .WithValidation<ReportPostRequest>()
            .Produces<ReportPostResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

    /// <summary>
    /// One report per user and post (409 <c>Social.AlreadyReported</c> after that); not on one's own post. Every report puts the post in the
    /// review queue (its <c>ReportCount</c> since the last review). Only a trusted reporter's report counts toward the automatic hide
    /// (<see cref="ReportRules.Counts"/>: account old enough, onboarding done, once per author a day), and <c>Reports:HideAt</c> counting reports
    /// hide a live post until a reviewer decides (security review M4). The reporter learns nothing about either.
    /// </summary>
    private static async Task<IResult> Handle(
        Guid id,
        ReportPostRequest request,
        ICurrentUser currentUser,
        KombinDbContext db,
        IClock clock,
        IOptions<ReportOptions> options,
        CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId;
        var post = await PostAccess.FindVisibleAsync(db, id, userId, tracking: true, cancellationToken);
        if (post is null)
        {
            return SocialErrors.PostNotFound.ToHttpResult();
        }

        if (post.AuthorId == userId)
        {
            return SocialErrors.CannotTargetSelf.ToHttpResult();
        }

        var now = clock.UtcNow;
        var rules = options.Value;
        var accountCreatedAt = await db.Users.Where(user => user.Id == userId).Select(user => user.CreatedAt).SingleAsync(cancellationToken);
        var personas = await db.Personas.CountAsync(persona => persona.UserId == userId && !persona.IsArchived, cancellationToken);
        var authorId = post.AuthorId;
        var windowStart = now - rules.PerAuthorWindow;
        var countedForAuthor = await db.PostReports.AnyAsync(
            report => report.ReporterId == userId && report.CountsTowardHide && report.CreatedAt > windowStart && db.Posts.Any(other => other.Id == report.PostId && other.AuthorId == authorId),
            cancellationToken);

        db.PostReports.Add(new PostReport
        {
            PostId = id,
            ReporterId = userId,
            Reason = EnumNames.TryParse<ReportReason>(request.Reason, out var reason) ? reason : ReportReason.Other,
            CreatedAt = now,
            CountsTowardHide = ReportRules.Counts(accountCreatedAt, personas, countedForAuthor, now, rules),
        });

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return SocialErrors.AlreadyReported.ToHttpResult();
        }

        // Reports since the last review: all of them for the queue, the counting ones for the hide (an approved post starts again from zero).
        var reviewedAt = post.ReviewedAt;
        var sinceReview = db.PostReports.Where(report => report.PostId == id && (reviewedAt == null || report.CreatedAt > reviewedAt));
        post.ReportCount = await sinceReview.CountAsync(cancellationToken);
        if (PostRules.ShouldHide(post.Status, await sinceReview.CountAsync(report => report.CountsTowardHide, cancellationToken), rules.HideAt))
        {
            post.Status = PostStatus.Hidden;
            post.HiddenAt = now;
        }

        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.Accepted((string?)null, new ReportPostResponse(true));
    }
}
