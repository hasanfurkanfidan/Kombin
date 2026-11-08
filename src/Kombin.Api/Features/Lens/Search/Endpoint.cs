using System.Text.Json.Serialization;
using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Results;
using Kombin.Api.Common.Storage;
using Kombin.Api.Features.Catalog;
using Kombin.Api.Features.Engagement;
using Kombin.Api.Features.Recommendation;
using Kombin.Api.Features.Wardrobe;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Catalog;
using Microsoft.AspNetCore.Mvc;
using Pgvector;

namespace Kombin.Api.Features.Lens.Search;

/// <summary>Fractions of the photo (0-1): left, top, width, height.</summary>
internal sealed record LensBox(double X, double Y, double W, double H);

internal sealed record LensCategoryGuess(string Slug, double Confidence);

/// <summary>The product's cheapest sellable offer by effective price. <c>GoPath</c> (<c>src=lens</c>) is the only way to open it.</summary>
internal sealed record LensOfferResponse(Guid OfferId, SellerRef? Seller, decimal Price, string GoPath);

/// <summary>
/// One catalog match. <c>Product</c> is the catalog list item. <c>Similarity</c> (0-1) is for analytics buckets and ordering; the app shows
/// <c>Level</c> (<c>close</c> "yakın eşleşme", <c>similar</c> "benzer"), never a percentage. <c>Offer</c> is null when nothing can be bought now.
/// </summary>
internal sealed record LensResultResponse(ProductListItem Product, double Similarity, string Level, bool InStock, LensOfferResponse? Offer);

/// <summary>One of the caller's own pieces close to the region: <c>exact</c> or <c>similar</c>, as in Social.</summary>
internal sealed record LensWardrobeMatchResponse(WardrobeItemSummary Item, double Similarity, string Match);

/// <summary>
/// A garment found in the photo. <c>Category</c>, <c>Slot</c>, <c>Family</c> and <c>Color</c> are null for the whole-photo region (nothing was
/// detected). <c>FamilyRestricted</c> is false when the search was widened (low confidence) or covered every family. An empty
/// <c>Results</c> is an honest "no close product". <c>WardrobeMatches</c> is present only for a signed-in caller.
/// </summary>
internal sealed record LensRegionResponse(
    int Index,
    LensBox Box,
    string? Slot,
    CategoryFamily? Family,
    LensCategoryGuess? Category,
    ProductColor? Color,
    bool FamilyRestricted,
    IReadOnlyList<LensResultResponse> Results,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<LensWardrobeMatchResponse>? WardrobeMatches);

/// <summary>
/// Regions with results come first (best match first), then the empty ones, so the app can select the first. <c>Audience</c> is the filter
/// that was applied (null: none); <c>WholePhoto</c> is true when no garment region was found and the photo was searched as a whole.
/// </summary>
internal sealed record LensSearchResponse(IReadOnlyList<LensRegionResponse> Regions, Audience? Audience, bool WholePhoto);

internal static class SearchEndpoint
{
    public const string AllAudiences = "all";

    /// <summary>The photo limit plus room for the multipart framing.</summary>
    public const int MaxRequestBytes = (int)WardrobeLimits.MaxImageBytes + (64 * 1024);

    private static readonly HashSet<string> AcceptedTypes = new(["image/jpeg", "image/png", "image/webp", "image/heic", "image/heif"], StringComparer.OrdinalIgnoreCase);

    private sealed record Region(LensBox Box, string? Slot, string? CategorySlug, double Confidence, IReadOnlyList<string> Candidates, string? Color, float[] Embedding);

    /// <summary>
    /// <c>POST /api/v1/lens/search?audience=</c>, multipart field <c>image</c> (≤ 8 MB): the garments in the photo and, for each, the closest
    /// catalog products with their cheapest seller, plus the caller's similar pieces when signed in. Anonymous works (an acquisition screen).
    /// The photo is held in memory for the request only: it is sent to <c>kombin-vision</c> and never stored or logged; adding a region to
    /// the wardrobe goes through the wardrobe's own upload flow with the crop. <c>audience</c>: a value of <see cref="Audience"/>, or
    /// <c>all</c> to drop the filter; left out, a signed-in caller's profile audience (Women or Men) applies.
    /// </summary>
    public static void Map(RouteGroupBuilder group) =>
        group.MapPost("/search", Handle)
            .WithName("LensSearch")
            .DisableAntiforgery()

            // The form is buffered in memory, never in a temp file (ASP.NET's default above 64 KB): the photo must not touch a disk.
            // Kestrel refuses larger bodies outright, so the buffer is bounded.
            .WithMetadata(new RequestSizeLimitAttribute(MaxRequestBytes))
            .WithMetadata(new RequestFormLimitsAttribute { MemoryBufferThreshold = MaxRequestBytes, MultipartBodyLengthLimit = MaxRequestBytes })
            .Accepts<IFormFile>("multipart/form-data")
            .Produces<LensSearchResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

    private static async Task<IResult> Handle(
        IFormFile? image,
        [FromQuery] string? audience,
        ICurrentUser currentUser,
        KombinDbContext db,
        IVisionClient vision,
        IObjectStorage storage,
        CancellationToken cancellationToken)
    {
        if (image is null || image.Length == 0)
        {
            return LensErrors.ImageMissing.ToHttpResult();
        }

        if (image.Length > WardrobeLimits.MaxImageBytes)
        {
            return LensErrors.ImageTooLarge.ToHttpResult();
        }

        if (!AcceptedTypes.Contains(image.ContentType ?? string.Empty))
        {
            return LensErrors.UnsupportedImage.ToHttpResult();
        }

        Audience? filter;
        if (string.IsNullOrWhiteSpace(audience))
        {
            filter = currentUser.UserIdOrNull is { } signedIn ? await ProfileAudienceAsync(db, signedIn, cancellationToken) : null;
        }
        else if (string.Equals(audience.Trim(), AllAudiences, StringComparison.OrdinalIgnoreCase))
        {
            filter = null;
        }
        else if (Enum.TryParse<Audience>(audience.Trim(), ignoreCase: true, out var parsed) && Enum.IsDefined(parsed))
        {
            filter = parsed;
        }
        else
        {
            return LensErrors.InvalidAudience.ToHttpResult();
        }

        byte[] photo;
        await using (var stream = image.OpenReadStream())
        using (var buffer = new MemoryStream((int)image.Length))
        {
            await stream.CopyToAsync(buffer, cancellationToken);
            photo = buffer.ToArray();
        }

        var detection = await vision.DetectAsync(photo, image.ContentType!, embed: true, cancellationToken);
        if (detection.Value is not { } detected)
        {
            return (detection.IsTransient ? LensErrors.VisionUnavailable : LensErrors.UnsupportedImage).ToHttpResult();
        }

        var model = await vision.GetEmbeddingModelAsync(cancellationToken);
        var regions = Regions(detected);
        if (model is null || regions is null)
        {
            return LensErrors.VisionUnavailable.ToHttpResult();
        }

        var wholePhoto = regions.Count == 1 && regions[0].CategorySlug is null;
        var categories = await db.Categories.AsNoTracking().Select(category => new { category.Id, category.Slug }).ToListAsync(cancellationToken);
        var audiences = filter is { } chosen ? LensRanking.AudiencesFor(chosen) : null;
        var viewerId = currentUser.UserIdOrNull;

        var neighbours = new List<List<(Guid ProductId, string SourceImageUrl, double Distance)>>();
        var restricted = new List<bool>();
        var wardrobe = new List<List<WardrobeNeighbour>>();

        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            await VisualIndex.TuneAsync(db, cancellationToken);

            foreach (var region in regions)
            {
                var families = LensRanking.FamiliesFor(region.CategorySlug, region.Confidence, region.Candidates);
                var categoryIds = families is null
                    ? null
                    : categories.Where(category => LensRanking.FamilyOf(category.Slug) is { } family && families.Contains(family)).Select(category => category.Id).ToList();
                var query = new Vector(region.Embedding);

                neighbours.Add(await VisualIndex.NearestProductsAsync(db, query, model, categoryIds, audiences, LensRanking.Candidates, cancellationToken));
                restricted.Add(families is { Count: 1 });
                wardrobe.Add(viewerId is { } userId
                    ? await VisualIndex.NearestWardrobeAsync(db, userId, query, model, LensRanking.MaxWardrobeMatches, cancellationToken)
                    : []);
            }

            await transaction.CommitAsync(cancellationToken);
        }

        var offers = await OutfitSources.CheapestOffersAsync(db, [.. neighbours.SelectMany(found => found).Select(found => found.ProductId).Distinct()], cancellationToken);
        var ranked = neighbours
            .Select((found, index) => LensRanking.Rank(
                found.Select(row => new LensCandidate(row.ProductId, row.SourceImageUrl, row.Distance, offers.TryGetValue(row.ProductId, out var offer) ? offer.Price : null)),
                wholePhoto ? LensRanking.WholePhotoFloor : LensRanking.SimilarityFloor))
            .ToList();

        var productIds = ranked.SelectMany(results => results).Select(result => result.ProductId).Distinct().ToList();
        var cards = await ProductSummaries.LoadAsync(db, productIds, cancellationToken);
        var offerIds = productIds.Where(offers.ContainsKey).Select(id => offers[id].OfferId).ToList();
        var sellers = await db.Offers.AsNoTracking()
            .Where(offer => offerIds.Contains(offer.Id))
            .Select(offer => new { offer.Id, offer.Seller.Name, offer.Seller.Slug })
            .ToDictionaryAsync(offer => offer.Id, offer => new SellerRef(offer.Name, offer.Slug), cancellationToken);

        var itemIds = wardrobe.SelectMany(found => found).Select(found => found.ItemId).Distinct().ToList();
        var items = itemIds.Count == 0 ? [] : await db.WardrobeItems.AsNoTracking().Where(item => itemIds.Contains(item.Id)).ToListAsync(cancellationToken);
        var summaries = (await WardrobeItemResponses.SummarizeAsync(db, storage, items, cancellationToken)).ToDictionary(summary => summary.Id);

        var responses = new List<(double Best, LensRegionResponse Response)>();
        for (var index = 0; index < regions.Count; index++)
        {
            var region = regions[index];
            var results = ranked[index]
                .Where(result => cards.ContainsKey(result.ProductId))
                .Select(result =>
                {
                    LensOfferResponse? offer = null;
                    if (offers.TryGetValue(result.ProductId, out var cheapest))
                    {
                        offer = new LensOfferResponse(cheapest.OfferId, sellers.GetValueOrDefault(cheapest.OfferId), cheapest.Price, GoPath(cheapest.OfferId));
                    }

                    var similarity = Math.Round(result.Similarity, 3);
                    return new LensResultResponse(cards[result.ProductId].ToListItem(), similarity, LensRanking.Level(result.Similarity), result.Sellable, offer);
                })
                .ToList();

            var resultProducts = results.Select(result => result.Product.Id).ToHashSet();
            List<LensWardrobeMatchResponse>? matches = viewerId is null
                ? null
                : [
                    .. wardrobe[index]
                        .Select(found => (Found: found, Match: LensRanking.WardrobeMatch(found.Similarity, found.ProductId is { } owned && resultProducts.Contains(owned))))
                        .Where(found => found.Match is not null && summaries.ContainsKey(found.Found.ItemId))
                        .Select(found => new LensWardrobeMatchResponse(summaries[found.Found.ItemId], Math.Round(found.Found.Similarity, 3), found.Match!)),
                ];

            var response = new LensRegionResponse(
                0,
                region.Box,
                region.Slot,
                LensRanking.FamilyOf(region.CategorySlug),
                region.CategorySlug is null ? null : new LensCategoryGuess(region.CategorySlug, Math.Round(region.Confidence, 3)),
                Enum.TryParse<ProductColor>(region.Color, out var color) && Enum.IsDefined(color) ? color : null,
                restricted[index],
                results,
                matches);
            responses.Add((results.Count == 0 ? -1 : results[0].Similarity, response));
        }

        // Stable: equals keep the detector's order (top to bottom for a person).
        var ordered = responses
            .Select((entry, position) => (entry.Best, entry.Response, Position: position))
            .OrderByDescending(entry => entry.Best >= 0)
            .ThenByDescending(entry => entry.Best)
            .ThenBy(entry => entry.Position)
            .Select((entry, index) => entry.Response with { Index = index })
            .ToList();

        return Results.Ok(new LensSearchResponse(ordered, filter, wholePhoto));
    }

    public static string GoPath(Guid offerId) => $"/api/v1/go/{offerId}?src=lens";

    /// <summary>The detected regions with an embedding, or the whole photo when there is none; null when the answer carries no embedding at all.</summary>
    private static List<Region>? Regions(VisionDetection detection)
    {
        var regions = detection.Regions
            .Where(region => region.Embedding is { Length: ProductEmbedding.Dimensions })
            .Select(region => new Region(
                new LensBox(region.Box.X, region.Box.Y, region.Box.W, region.Box.H),
                region.Slot,
                region.Category.Slug,
                region.Category.Confidence,
                [.. region.CategoryCandidates.Select(candidate => candidate.Slug)],
                region.Color.Name,
                region.Embedding!))
            .ToList();

        if (regions.Count > 0)
        {
            return regions;
        }

        return detection.Embedding is { Length: ProductEmbedding.Dimensions } whole
            ? [new Region(new LensBox(0, 0, 1, 1), null, null, 0, [], null, whole)]
            : null;
    }

    /// <summary>A profile's Women or Men; Unisex (the default) and a missing profile mean no filter.</summary>
    private static async Task<Audience?> ProfileAudienceAsync(KombinDbContext db, Guid userId, CancellationToken cancellationToken)
    {
        var audience = await db.UserProfiles.AsNoTracking()
            .Where(profile => profile.UserId == userId)
            .Select(profile => (Audience?)profile.Audience)
            .SingleOrDefaultAsync(cancellationToken);

        return audience is Audience.Women or Audience.Men ? audience : null;
    }
}
