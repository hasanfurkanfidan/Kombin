using Kombin.Api.Common.Auth;
using Kombin.Api.Common.RateLimiting;
using Kombin.Api.Common.Storage;
using Kombin.Api.Common.Validation;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Wardrobe;

namespace Kombin.Api.Features.Wardrobe.CreateUploadUrl;

internal sealed record CreateUploadUrlRequest(string? ContentType, long SizeBytes);

/// <summary>PUT the photo to <c>UploadUrl</c> with exactly <c>Headers</c> before <c>ExpiresAt</c>, then call <c>process</c>.</summary>
internal sealed record CreateUploadUrlResponse(Guid ItemId, string UploadUrl, IReadOnlyDictionary<string, string> Headers, DateTimeOffset ExpiresAt);

internal static class CreateUploadUrlEndpoint
{
    public static void Map(RouteGroupBuilder group) =>
        group.MapPost("/items/upload-url", Handle)
            .WithName("CreateWardrobeUploadUrl")
            .WithValidation<CreateUploadUrlRequest>()
            .RequireRateLimiting(RateLimitPolicies.Uploads)
            .Produces<CreateUploadUrlResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized);

    private static async Task<IResult> Handle(
        CreateUploadUrlRequest request, ICurrentUser currentUser, KombinDbContext db, IObjectStorage storage, CancellationToken cancellationToken)
    {
        // The bare media type: it is signed into the URL, and the client has to send it back verbatim.
        var contentType = request.ContentType!.Split(';')[0].Trim().ToLowerInvariant();
        var item = new WardrobeItem { UserId = currentUser.UserId, Status = WardrobeItemStatus.Processing };
        item.ImageOriginalKey = StorageKeys.Build(WardrobeLimits.StorageArea, item.UserId, item.Id, ImageVariant.Original, StorageKeys.ExtensionFor(contentType)!);

        db.WardrobeItems.Add(item);
        await db.SaveChangesAsync(cancellationToken);

        var upload = storage.GetPresignedUploadUrl(item.ImageOriginalKey, contentType, WardrobeLimits.UploadUrlLifetime);

        return TypedResults.Created(
            $"/api/v1/me/wardrobe/items/{item.Id}",
            new CreateUploadUrlResponse(item.Id, upload.Url, upload.Headers, upload.ExpiresAt));
    }
}
