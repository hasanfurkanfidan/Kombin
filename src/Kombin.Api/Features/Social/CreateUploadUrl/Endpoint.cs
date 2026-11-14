using Kombin.Api.Common.Auth;
using Kombin.Api.Common.RateLimiting;
using Kombin.Api.Common.Results;
using Kombin.Api.Common.Storage;
using Kombin.Api.Common.Validation;
using Kombin.Api.Features.Social.Consent;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Social;

namespace Kombin.Api.Features.Social.CreateUploadUrl;

internal sealed record CreatePostUploadUrlRequest(string? ContentType, long SizeBytes);

/// <summary>PUT the photo to <c>UploadUrl</c> with exactly <c>Headers</c> before <c>ExpiresAt</c>, then call <c>process</c>.</summary>
internal sealed record CreatePostUploadUrlResponse(Guid PostId, string UploadUrl, IReadOnlyDictionary<string, string> Headers, DateTimeOffset ExpiresAt);

internal static class CreatePostUploadUrlEndpoint
{
    public static void Map(RouteGroupBuilder group) =>
        group.MapPost("/upload-url", Handle)
            .WithName("CreatePostUploadUrl")
            .WithValidation<CreatePostUploadUrlRequest>()
            .RequireRateLimiting(RateLimitPolicies.Uploads)
            .Produces<CreatePostUploadUrlResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

    /// <summary>Creates a private <c>Processing</c> photo post; refused (403 <c>Social.ConsentRequired</c>) until the current consent is accepted.</summary>
    private static async Task<IResult> Handle(
        CreatePostUploadUrlRequest request, ICurrentUser currentUser, KombinDbContext db, IObjectStorage storage, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId;
        if (await ConsentEndpoint.AcceptedAtAsync(db, userId, cancellationToken) is not { } consentAt)
        {
            return SocialErrors.ConsentRequired.ToHttpResult();
        }

        var contentType = request.ContentType!.Split(';')[0].Trim().ToLowerInvariant();
        var post = new Post
        {
            AuthorId = userId,
            Kind = PostKind.Photo,
            Audience = await AudienceOfAsync(db, userId, cancellationToken),
            ConsentVersion = PostingConsent.Current.Version,
            ConsentAt = consentAt,
            ContentType = contentType,
        };
        post.ImageOriginalKey = StorageKeys.Build(SocialLimits.StorageArea, userId, post.Id, ImageVariant.Original, StorageKeys.ExtensionFor(contentType)!);

        db.Posts.Add(post);
        await db.SaveChangesAsync(cancellationToken);

        var upload = storage.GetPresignedUploadUrl(post.ImageOriginalKey, contentType, SocialLimits.UploadUrlLifetime);

        return TypedResults.Created($"/api/v1/posts/{post.Id}", new CreatePostUploadUrlResponse(post.Id, upload.Url, upload.Headers, upload.ExpiresAt));
    }

    /// <summary>The author's audience (the for-you feed matches it); unisex without a profile.</summary>
    public static async Task<Audience> AudienceOfAsync(KombinDbContext db, Guid userId, CancellationToken cancellationToken) =>
        await db.UserProfiles.AsNoTracking().Where(profile => profile.UserId == userId).Select(profile => (Audience?)profile.Audience).SingleOrDefaultAsync(cancellationToken)
        ?? Audience.Unisex;
}
