using Kombin.Api.Common.Auth;
using Kombin.Api.Common.RateLimiting;
using Kombin.Api.Common.Results;
using Kombin.Api.Common.Storage;
using Kombin.Api.Common.Time;
using Kombin.Api.Common.Validation;
using Kombin.Api.Features.Wardrobe.BulkAdd;
using Kombin.Api.Features.Wardrobe.CreateUploadUrl;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Wardrobe;
using Microsoft.Extensions.Options;

namespace Kombin.Api.Features.Wardrobe.BulkCreateUploadUrl;

/// <summary>PUT the photo to <c>UploadUrl</c> with exactly <c>Headers</c> before <c>ExpiresAt</c>, then call <c>process</c> on the batch.</summary>
internal sealed record BulkCreateUploadUrlResponse(Guid BatchId, string UploadUrl, IReadOnlyDictionary<string, string> Headers, DateTimeOffset ExpiresAt);

internal static class BulkCreateUploadUrlEndpoint
{
    public static void Map(RouteGroupBuilder group) =>
        group.MapPost("/upload-url", Handle)
            .WithName("CreateWardrobeBulkUploadUrl")
            .WithSummary("Starts a bulk add: creates a batch and a presigned PUT for the photo of several garments.")
            .WithValidation<CreateUploadUrlRequest>()
            .RequireRateLimiting(RateLimitPolicies.Uploads)
            .Produces<BulkCreateUploadUrlResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

    /// <summary>
    /// The single add's upload step for one batch: the same request, validator (content types, 1 byte to 8 MB) and presigned PUT. At most
    /// <c>BulkAdd:MaxOpenBatches</c> batches may be open (409 <c>Wardrobe.BulkTooManyOpen</c>); the count and the insert share one
    /// transaction under a per-user advisory lock, so two taps at once cannot both take the last place.
    /// </summary>
    private static async Task<IResult> Handle(
        CreateUploadUrlRequest request,
        ICurrentUser currentUser,
        KombinDbContext db,
        IObjectStorage storage,
        IClock clock,
        IOptions<BulkAddOptions> options,
        CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId;
        var now = clock.UtcNow;
        var contentType = request.ContentType!.Split(';')[0].Trim().ToLowerInvariant();

        var batch = new BulkBatch { UserId = userId, Status = BulkBatchStatus.Created, ExpiresAt = now + options.Value.StagingRetention };
        batch.OriginalKey = StorageKeys.BuildBulk(userId, batch.Id, batch.Id, ImageVariant.Original, StorageKeys.ExtensionFor(contentType)!);

        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({$"bulk-open:{userId:N}"}, 0))", cancellationToken);

            var ended = BulkAddRules.EndedStatuses;
            var open = await db.BulkBatches.CountAsync(candidate => candidate.UserId == userId && !ended.Contains(candidate.Status) && candidate.ExpiresAt > now, cancellationToken);
            if (open >= options.Value.MaxOpenBatches)
            {
                return BulkAddErrors.TooManyOpen.ToHttpResult();
            }

            db.BulkBatches.Add(batch);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        var upload = storage.GetPresignedUploadUrl(batch.OriginalKey, contentType, WardrobeLimits.UploadUrlLifetime);

        return TypedResults.Created(
            $"/api/v1/me/wardrobe/bulk/{batch.Id}",
            new BulkCreateUploadUrlResponse(batch.Id, upload.Url, upload.Headers, upload.ExpiresAt));
    }
}
