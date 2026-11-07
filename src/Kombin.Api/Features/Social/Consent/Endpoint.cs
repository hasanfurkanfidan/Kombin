using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Results;
using Kombin.Api.Common.Time;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Social;

namespace Kombin.Api.Features.Social.Consent;

/// <summary>The current posting consent and, when the caller accepted this version, when.</summary>
internal sealed record ConsentResponse(string Version, string Text, DateTimeOffset? AcceptedAt);

/// <summary>The version the user saw and accepted; only the current one is accepted.</summary>
internal sealed record AcceptConsentRequest(string? Version);

/// <summary><c>GET</c> and <c>POST /api/v1/social/consent</c>: the versioned consent shown before the first post.</summary>
internal static class ConsentEndpoint
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/consent", GetAsync)
            .WithName("GetPostingConsent")
            .Produces<ConsentResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        group.MapPost("/consent", AcceptAsync)
            .WithName("AcceptPostingConsent")
            .Produces<ConsentResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized);
    }

    /// <summary>The time the user accepted the current version, or null.</summary>
    public static Task<DateTimeOffset?> AcceptedAtAsync(KombinDbContext db, Guid userId, CancellationToken cancellationToken)
    {
        var version = PostingConsent.Current.Version;

        return db.PostingConsents.AsNoTracking()
            .Where(consent => consent.UserId == userId && consent.Version == version)
            .OrderBy(consent => consent.AcceptedAt)
            .Select(consent => (DateTimeOffset?)consent.AcceptedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private static async Task<IResult> GetAsync(ICurrentUser currentUser, KombinDbContext db, CancellationToken cancellationToken)
    {
        var current = PostingConsent.Current;

        return TypedResults.Ok(new ConsentResponse(current.Version, current.Text, await AcceptedAtAsync(db, currentUser.UserId, cancellationToken)));
    }

    /// <summary>Idempotent: accepting the same version again keeps the first acceptance. The stored row holds the exact text that was shown.</summary>
    private static async Task<IResult> AcceptAsync(
        AcceptConsentRequest request, ICurrentUser currentUser, KombinDbContext db, IClock clock, CancellationToken cancellationToken)
    {
        var current = PostingConsent.Current;
        if (request.Version != current.Version)
        {
            return SocialErrors.ConsentOutdated.ToHttpResult();
        }

        var acceptedAt = await AcceptedAtAsync(db, currentUser.UserId, cancellationToken);
        if (acceptedAt is null)
        {
            acceptedAt = clock.UtcNow;
            db.PostingConsents.Add(new PostingConsentRecord { UserId = currentUser.UserId, Version = current.Version, Text = current.Text, AcceptedAt = acceptedAt.Value });
            await db.SaveChangesAsync(cancellationToken);
        }

        return TypedResults.Ok(new ConsentResponse(current.Version, current.Text, acceptedAt));
    }
}
