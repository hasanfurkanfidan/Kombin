using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Results;
using Kombin.Api.Common.Time;
using Kombin.Api.Common.Validation;
using Kombin.Api.Features.Catalog;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Social;
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
    /// One report per user and post (409 <c>Social.AlreadyReported</c> after that); not on one's own post. The count is of distinct reporters,
    /// and the <see cref="PostRules.HideAtReports"/>th hides a live post until a reviewer decides. The reporter learns nothing about that.
    /// </summary>
    private static async Task<IResult> Handle(
        Guid id, ReportPostRequest request, ICurrentUser currentUser, KombinDbContext db, IClock clock, CancellationToken cancellationToken)
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
        db.PostReports.Add(new PostReport { PostId = id, ReporterId = userId, Reason = EnumNames.TryParse<ReportReason>(request.Reason, out var reason) ? reason : ReportReason.Other, CreatedAt = now });

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return SocialErrors.AlreadyReported.ToHttpResult();
        }

        // Reports since the last review: an approved post needs three new reporters to be hidden again.
        var reviewedAt = post.ReviewedAt;
        post.ReportCount = await db.PostReports.CountAsync(report => report.PostId == id && (reviewedAt == null || report.CreatedAt > reviewedAt), cancellationToken);
        if (PostRules.ShouldHide(post.Status, post.ReportCount))
        {
            post.Status = PostStatus.Hidden;
            post.HiddenAt = now;
        }

        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.Accepted((string?)null, new ReportPostResponse(true));
    }
}
