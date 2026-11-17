using System.Globalization;
using System.Text.Json;
using Hangfire;
using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Http;
using Kombin.Api.Common.RateLimiting;
using Kombin.Api.Common.Results;
using Kombin.Api.Common.Storage;
using Kombin.Api.Common.Time;
using Kombin.Api.Common.Validation;
using Kombin.Api.Features.Recommendation;
using Kombin.Api.Features.TryOn.Jobs;
using Kombin.Api.Features.TryOn.Rules;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Recommendation;
using Kombin.Api.Persistence.Entities.TryOn;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Kombin.Api.Features.TryOn.GetTryOn;

/// <summary>
/// Exactly one of <c>OutfitId</c> (one of the caller's outfits: daily, how-to-wear, saved) and <c>ProductId</c>. <c>WithWardrobe</c> (product
/// only): "Dolabımla dene", the product with the caller's own pieces; without it the product alone (never drawable: no bottom or no top).
/// <c>Check</c>: only say whether the drawing would be available (see <see cref="GetTryOnEndpoint.Description"/>).
/// </summary>
internal sealed record TryOnQuery(Guid? OutfitId, Guid? ProductId, bool? WithWardrobe, bool? Check);

/// <summary>
/// "Manken üstünde gör". <c>Status</c>: <c>Ready</c> (show <c>ImageUrl</c>, a presigned GET valid for an hour, with "Manken üzerinde yaklaşık
/// görünüm"), <c>Preparing</c> (show "Hazırlanıyor" and ask again), <c>Unavailable</c> (hide the action entirely, never a disabled button).
/// <c>Garments</c> (Ready only): each drawn piece by the ID the outfit gives it (wardrobe item or product) with its fit label, null when nothing
/// was comparable. <c>DefaultedMeasurements</c> (Ready only): the measurements the mannequin guessed; not empty → "Ölçülerini ekle, manken sana
/// benzesin". <c>Reason</c> (Unavailable only): why, for the app team and support. <c>Renderer</c> (Ready with an image only): the name of the
/// renderer that drew it, sent back as <c>renderer</c> of <c>POST /me/tryon/reports</c> ("Yanlış görünüyor"); null otherwise, a check included.
/// </summary>
internal sealed record TryOnResponse(
    TryOnStatus Status,
    string? ImageUrl,
    IReadOnlyList<TryOnGarmentResponse> Garments,
    IReadOnlyList<string> DefaultedMeasurements,
    TryOnUnavailableReason? Reason,
    string? Renderer = null);

internal static class GetTryOnEndpoint
{
    public const string Description =
        "\"Manken üstünde gör\": the outfit drawn on a mannequin proportioned from the caller's measurements. Ready carries a presigned image URL "
        + "(one hour); Preparing means a garment is still being prepared (ask again); Unavailable means the action must be hidden. A render reveals "
        + "body proportions: the image is private to the caller. With check=true nothing is drawn and no render limit is spent: the answer is "
        + "the status and reason only (Ready means a real request would draw it; imageUrl, garments and renderer stay empty), the way to decide "
        + "whether to show the action. Ready carries renderer, which the app sends back with a \"Yanlış görünüyor\" report.";

    /// <summary>How many how-to-wear outfits "Dolabımla dene" looks through for one made of the caller's own pieces only.</summary>
    public const int WardrobeOutfits = 3;

    public static void Map(RouteGroupBuilder group) =>
        group.MapGet(string.Empty, Handle)
            .WithName("GetTryOn")
            .WithDescription(Description)
            .WithValidation<TryOnQuery>()
            .Produces<TryOnResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

    private static async Task<IResult> Handle(
        [AsParameters] TryOnQuery query,
        ICurrentUser currentUser,
        KombinDbContext db,
        IClock clock,
        IObjectStorage storage,
        OutfitStore store,
        TryOnRenderer renderer,
        IBackgroundJobClient jobs,
        IOptions<TryOnOptions> options,
        CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId;
        var now = clock.UtcNow;

        var refs = await PiecesAsync(db, store, userId, query, now, cancellationToken);
        if (refs.IsFailure)
        {
            return refs.Error.ToHttpResult();
        }

        var pieces = await TryOnPieces.LoadAsync(db, userId, refs.Value, cancellationToken);
        var body = TryOnBody.From(await db.UserProfiles.AsNoTracking().SingleOrDefaultAsync(profile => profile.UserId == userId, cancellationToken));
        var decision = TryOnReadiness.Decide([.. pieces.Select(piece => piece.Facts)], body.Bmi, options.Value.MaxBmi);

        if (decision.Prepare.Count > 0)
        {
            await RequestPrepareAsync(db, jobs, pieces.Where(piece => decision.Prepare.Contains(piece.Ref.Id)).ToList(), now, options.Value.RequestInterval, cancellationToken);
        }

        if (decision.Status != TryOnStatus.Ready)
        {
            return TypedResults.Ok(new TryOnResponse(decision.Status, null, [], [], decision.Reason));
        }

        if (query.Check == true)
        {
            // Would it be available? The readiness rule above plus a refusal the renderer already gave for these exact inputs; no drawing, no gate.
            return await renderer.CheckAsync(userId, body, pieces, cancellationToken) == RenderVerdict.Refused
                ? TypedResults.Ok(new TryOnResponse(TryOnStatus.Unavailable, null, [], [], TryOnUnavailableReason.RendererRefused))
                : TypedResults.Ok(new TryOnResponse(TryOnStatus.Ready, null, [], [], null));
        }

        var result = await renderer.RenderAsync(userId, body, pieces, now, cancellationToken);

        return result.Verdict switch
        {
            RenderVerdict.Rendered => TypedResults.Ok(new TryOnResponse(
                TryOnStatus.Ready,
                storage.GetPresignedDownloadUrl(result.Render!.ImageKey!, options.Value.RenderUrlLifetime),
                JsonSerializer.Deserialize<List<TryOnGarmentResponse>>(result.Render.GarmentsJson, TryOnRenderer.Json) ?? [],
                result.Render.DefaultedMeasurements,
                null,
                result.Render.Renderer)),
            RenderVerdict.InFlight => TypedResults.Ok(new TryOnResponse(TryOnStatus.Preparing, null, [], [], null)),
            RenderVerdict.Refused => TypedResults.Ok(new TryOnResponse(TryOnStatus.Unavailable, null, [], [], TryOnUnavailableReason.RendererRefused)),
            RenderVerdict.OverLimit => new RenderLimitResult(result.RetryAfter),
            _ => TryOnErrors.RendererUnavailable.ToHttpResult(),
        };
    }

    /// <summary>The outfit's pieces in order; for "Dolabımla dene" the how-to-wear engine's first outfit made of the caller's own pieces only.</summary>
    private static async Task<Result<List<PieceRef>>> PiecesAsync(
        KombinDbContext db, OutfitStore store, Guid userId, TryOnQuery query, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (query.OutfitId is { } outfitId)
        {
            var items = await db.OutfitItems.AsNoTracking()
                .Where(item => item.OutfitId == outfitId && db.Outfits.Any(outfit => outfit.Id == outfitId && outfit.UserId == userId))
                .OrderBy(item => item.Position)
                .Select(item => new PieceRef(item.WardrobeItemId, item.ProductId))
                .ToListAsync(cancellationToken);

            return items.Count == 0 ? TryOnErrors.OutfitNotFound : items;
        }

        var productId = query.ProductId!.Value;
        if (!await db.Products.AnyAsync(product => product.Id == productId, cancellationToken))
        {
            return TryOnErrors.ProductNotFound;
        }

        if (query.WithWardrobe != true || await HowToWear.LoadAsync(db, userId, null, now, cancellationToken) is not { } context)
        {
            return new List<PieceRef> { new(null, productId) };
        }

        var suggested = await HowToWear.SuggestAsync(db, store, context, new HowToWearAnchor(productId, null), WardrobeOutfits, 0, null, cancellationToken);
        if (suggested.IsFailure)
        {
            return suggested.Error;
        }

        // Only the caller's own pieces besides the anchor: a shirt with no trousers in the wardrobe is the shirt alone (never bare legs).
        var outfit = suggested.Value.Outfits.FirstOrDefault(candidate => candidate.Completeness == OutfitCompleteness.Complete);

        return outfit is null
            ? new List<PieceRef> { new(null, productId) }
            : outfit.Items.OrderBy(item => item.Position).Select(item => new PieceRef(item.WardrobeItemId, item.ProductId)).ToList();
    }

    /// <summary>
    /// Creates the missing garment rows as <c>Pending</c>, marks them as asked for, and enqueues one targeted prepare; a row asked for less than
    /// <c>TryOn:RequestInterval</c> ago is left alone (the app polls).
    /// </summary>
    private static async Task RequestPrepareAsync(
        KombinDbContext db, IBackgroundJobClient jobs, List<TryOnPiece> pieces, DateTimeOffset now, TimeSpan interval, CancellationToken cancellationToken)
    {
        var asked = new List<TryOnPiece>();
        foreach (var piece in pieces.DistinctBy(piece => (piece.OwnerItemId, piece.OwnerProductId)))
        {
            if (piece.Row is null)
            {
                db.TryOnGarments.Add(new TryOnGarment
                {
                    WardrobeItemId = piece.OwnerItemId,
                    ProductId = piece.OwnerProductId,
                    Status = TryOnGarmentStatus.Pending,
                    RequestedAt = now,
                });
                try
                {
                    await db.SaveChangesAsync(cancellationToken);
                    asked.Add(piece);
                }
                catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
                {
                    // A parallel request (or the job) created it: that one asked.
                    db.ChangeTracker.Clear();
                }

                continue;
            }

            var since = now - interval;
            var marked = await db.TryOnGarments
                .Where(garment => garment.Id == piece.Row.Id && garment.Status == TryOnGarmentStatus.Pending && (garment.RequestedAt == null || garment.RequestedAt <= since))
                .ExecuteUpdateAsync(setters => setters.SetProperty(garment => garment.RequestedAt, now), cancellationToken);
            if (marked > 0)
            {
                asked.Add(piece);
            }
        }

        if (asked.Count > 0)
        {
            Guid[] items = [.. asked.Where(piece => piece.OwnerItemId is not null).Select(piece => piece.OwnerItemId!.Value)];
            Guid[] products = [.. asked.Where(piece => piece.OwnerProductId is not null).Select(piece => piece.OwnerProductId!.Value)];
            jobs.Enqueue<TryOnPrepareJob>(job => job.PrepareRequestedAsync(items, products, CancellationToken.None));
        }
    }

    /// <summary>The hourly render limit, in the rate limiter's shape: 429, <c>errorCode: "RateLimited"</c>, <c>retryAfterSeconds</c>, <c>Retry-After</c>.</summary>
    private sealed class RenderLimitResult(TimeSpan retryAfter) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            ArgumentNullException.ThrowIfNull(httpContext);

            var seconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
            httpContext.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);

            return Problems.Create(
                    httpContext,
                    StatusCodes.Status429TooManyRequests,
                    title: "Too many requests.",
                    detail: "Too many new mannequin drawings this hour. Try again after the time in the Retry-After header.",
                    errorCode: RateLimitingSetup.ErrorCode,
                    extra: new Dictionary<string, object?> { ["retryAfterSeconds"] = seconds })
                .ExecuteAsync(httpContext);
        }
    }
}
