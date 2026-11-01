using Kombin.Api.Common.Results;
using Kombin.Api.Common.Time;
using Kombin.Api.Common.Validation;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Catalog;

namespace Kombin.Api.Features.Catalog.UpsertOffers;

internal sealed record UpsertOffersRequest(string SellerSlug, IReadOnlyList<UpsertOfferItem> Items);

internal sealed record UpsertOfferItem(
    string ExternalId,
    string Url,
    string Title,
    decimal Price,
    decimal? OriginalPrice,
    string? Currency,
    decimal? ShippingCost,
    decimal? FreeShippingThreshold,
    string StockStatus,
    IReadOnlyList<string>? AvailableSizes,
    IReadOnlyList<string>? ImageUrls,
    UpsertProductInfo Product);

internal sealed record UpsertProductInfo(
    string BrandName,
    string Name,
    string? ModelCode,
    string CategorySlug,
    string Audience,
    string PrimaryColor,
    string? Pattern,
    string? Fit,
    int? Formality,
    IReadOnlyList<string>? Seasons,
    string? Material,
    string? Description,
    Guid? CanonicalProductId);

internal sealed record UpsertItemError(int Index, string ExternalId, string ErrorCode, string Message);

internal sealed record UpsertOffersResponse(int Created, int Updated, int SnapshotsWritten, IReadOnlyList<UpsertItemError> Errors);

internal static partial class UpsertOffersEndpoint
{
    private const short DefaultFormality = 3;

    public static void Map(RouteGroupBuilder group) =>
        group.MapPost("/offers/upsert", Handle)
            .WithName("UpsertOffers")
            .WithValidation<UpsertOffersRequest>()
            .Produces<UpsertOffersResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

    private static async Task<IResult> Handle(
        UpsertOffersRequest request,
        KombinDbContext db,
        IClock clock,
        ILogger<UpsertOffersRequest> logger,
        CancellationToken cancellationToken)
    {
        if (request.Items.Count > UpsertLimits.MaxItems)
        {
            return CatalogErrors.BatchTooLarge.ToHttpResult();
        }

        var sellerSlug = request.SellerSlug.Trim();

        // Unknown sellers are only created on demand for brand stores ("<brand-slug>-store"); anything else must be seeded.
        if (!sellerSlug.EndsWith(BrandStoreSuffix, StringComparison.Ordinal)
            && !await db.Sellers.AnyAsync(seller => seller.Slug == sellerSlug, cancellationToken))
        {
            return CatalogErrors.SellerNotFound.ToHttpResult();
        }

        var categories = await db.Categories.AsNoTracking().ToDictionaryAsync(category => category.Slug, category => category.Id, cancellationToken);
        var ingestion = new Ingestion(db, clock, logger, sellerSlug, categories);

        var created = 0;
        var updated = 0;
        var snapshotsWritten = 0;
        var errors = new List<UpsertItemError>();

        var indexed = request.Items.Select((item, index) => new IndexedItem(index, item));

        foreach (var group in indexed.Chunk(UpsertLimits.GroupSize))
        {
            foreach (var outcome in await ingestion.RunGroupWithFallbackAsync(group, cancellationToken))
            {
                if (outcome.Error is { } error)
                {
                    errors.Add(new UpsertItemError(outcome.Index, outcome.ExternalId, error.Code, error.Description));
                    continue;
                }

                created += outcome.Created ? 1 : 0;
                updated += outcome.Created ? 0 : 1;
                snapshotsWritten += outcome.SnapshotWritten ? 1 : 0;
            }
        }

        return Results.Ok(new UpsertOffersResponse(created, updated, snapshotsWritten, errors));
    }

    private const string BrandStoreSuffix = "-store";

    private readonly record struct IndexedItem(int Index, UpsertOfferItem Item);

    private readonly record struct ItemOutcome(int Index, string ExternalId, Error? Error, bool Created, bool SnapshotWritten);

    /// <summary>Everything a group of items learns while it runs; discarded if the group's transaction is rolled back.</summary>
    private sealed class GroupState
    {
        public Seller? Seller { get; set; }

        public required Dictionary<string, Offer> Offers { get; init; }

        public required Dictionary<Guid, PriceSnapshot> LastSnapshots { get; init; }
    }

    private sealed class Ingestion(
        KombinDbContext db,
        IClock clock,
        ILogger logger,
        string sellerSlug,
        IReadOnlyDictionary<string, Guid> categories)
    {
        /// <summary>
        /// Runs the group in one transaction. If anything throws, nothing from the group is kept and every item is re-run
        /// on its own, so good items still land and only the bad one is reported.
        /// </summary>
        public async Task<List<ItemOutcome>> RunGroupWithFallbackAsync(IndexedItem[] group, CancellationToken cancellationToken)
        {
            try
            {
                return await RunGroupAsync(group, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                db.ChangeTracker.Clear();
                LogGroupFailed(logger, exception, group[0].Index, group.Length);
            }

            var outcomes = new List<ItemOutcome>(group.Length);

            foreach (var entry in group)
            {
                try
                {
                    outcomes.AddRange(await RunGroupAsync([entry], cancellationToken));
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    db.ChangeTracker.Clear();
                    LogItemFailed(logger, exception, entry.Index, entry.Item.ExternalId);
                    outcomes.Add(new ItemOutcome(entry.Index, entry.Item.ExternalId, CatalogErrors.ItemFailed, Created: false, SnapshotWritten: false));
                }
            }

            return outcomes;
        }

        private async Task<List<ItemOutcome>> RunGroupAsync(IndexedItem[] group, CancellationToken cancellationToken)
        {
            // Disposing an uncommitted transaction rolls it back.
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            var state = await LoadStateAsync(group, cancellationToken);
            var now = clock.UtcNow;
            var outcomes = new List<ItemOutcome>(group.Length);

            foreach (var entry in group)
            {
                outcomes.Add(await ProcessItemAsync(state, entry, now, cancellationToken));
            }

            await transaction.CommitAsync(cancellationToken);
            db.ChangeTracker.Clear();
            return outcomes;
        }

        private async Task<GroupState> LoadStateAsync(IndexedItem[] group, CancellationToken cancellationToken)
        {
            var seller = await db.Sellers.SingleOrDefaultAsync(candidate => candidate.Slug == sellerSlug, cancellationToken);
            var offers = new Dictionary<string, Offer>(StringComparer.Ordinal);
            var lastSnapshots = new Dictionary<Guid, PriceSnapshot>();

            if (seller is not null)
            {
                var externalIds = group.Select(entry => entry.Item.ExternalId.Trim()).Distinct().ToList();

                offers = await db.Offers
                    .Include(offer => offer.Product)
                    .Where(offer => offer.SellerId == seller.Id && externalIds.Contains(offer.ExternalId))
                    .ToDictionaryAsync(offer => offer.ExternalId, StringComparer.Ordinal, cancellationToken);

                var offerIds = offers.Values.Select(offer => offer.Id).ToList();

                var latest = await db.PriceSnapshots
                    .AsNoTracking()
                    .Where(snapshot => offerIds.Contains(snapshot.OfferId)
                        && snapshot.ObservedAt == db.PriceSnapshots.Where(other => other.OfferId == snapshot.OfferId).Max(other => other.ObservedAt))
                    .ToListAsync(cancellationToken);

                lastSnapshots = latest
                    .GroupBy(snapshot => snapshot.OfferId)
                    .ToDictionary(snapshots => snapshots.Key, snapshots => snapshots.OrderByDescending(snapshot => snapshot.Id).First());
            }

            return new GroupState { Seller = seller, Offers = offers, LastSnapshots = lastSnapshots };
        }

        private async Task<ItemOutcome> ProcessItemAsync(GroupState state, IndexedItem entry, DateTimeOffset now, CancellationToken cancellationToken)
        {
            var item = entry.Item;
            var info = item.Product;
            var externalId = item.ExternalId.Trim();
            var stockStatus = Enum.Parse<StockStatus>(item.StockStatus, ignoreCase: true);

            // Every check that can reject the item happens before anything is added to the context, so a rejected
            // item leaves no trace in the group's transaction.
            Offer offer;
            Product product;
            Product? previousProduct = null;
            var isNewOffer = !state.Offers.TryGetValue(externalId, out var existing);

            if (isNewOffer)
            {
                if (state.Seller is null && Slugs.Slugify(info.BrandName) + BrandStoreSuffix != sellerSlug)
                {
                    return Fail(entry, CatalogErrors.SellerNotFound);
                }

                var resolved = await ResolveProductAsync(info, cancellationToken);
                if (resolved.IsFailure)
                {
                    return Fail(entry, resolved.Error);
                }

                product = resolved.Value;

                if (state.Seller is null)
                {
                    state.Seller = CreateBrandStore(info.BrandName.Trim(), item.Url);
                    db.Sellers.Add(state.Seller);
                }

                offer = new Offer { ProductId = product.Id, SellerId = state.Seller.Id, ExternalId = externalId };
                db.Offers.Add(offer);
                await AddMissingImagesAsync(product, item.ImageUrls, cancellationToken);
            }
            else
            {
                offer = existing!;
                product = offer.Product;

                // Rule 1: an existing listing keeps its product; only an explicit, different canonicalProductId re-links it.
                if (info.CanonicalProductId is { } canonicalId && canonicalId != offer.ProductId)
                {
                    var target = await db.Products.SingleOrDefaultAsync(candidate => candidate.Id == canonicalId, cancellationToken);
                    if (target is null)
                    {
                        return Fail(entry, CatalogErrors.ProductNotFound);
                    }

                    previousProduct = product;
                    offer.Product = target;
                    product = target;
                    ApplyMutableAttributes(product, info);
                    await AddMissingImagesAsync(product, item.ImageUrls, cancellationToken);
                }
            }

            ApplyListing(offer, item, stockStatus, now);
            product.IsActive = true;

            if (previousProduct is not null
                && !await db.Offers.AnyAsync(other => other.ProductId == previousProduct.Id && other.IsActive && other.Id != offer.Id, cancellationToken))
            {
                previousProduct.IsActive = false;
            }

            state.LastSnapshots.TryGetValue(offer.Id, out var last);
            var snapshotWritten = SnapshotRule.ShouldWrite(last, offer.CurrentPrice, offer.OriginalPrice, stockStatus, now);

            if (snapshotWritten)
            {
                var snapshot = new PriceSnapshot
                {
                    OfferId = offer.Id,
                    Price = offer.CurrentPrice,
                    OriginalPrice = offer.OriginalPrice,
                    StockStatus = stockStatus,
                    ObservedAt = now,
                };
                db.PriceSnapshots.Add(snapshot);
                state.LastSnapshots[offer.Id] = snapshot;
            }

            await db.SaveChangesAsync(cancellationToken);
            state.Offers[externalId] = offer;

            return new ItemOutcome(entry.Index, externalId, Error: null, isNewOffer, snapshotWritten);
        }

        /// <summary>Rules 2-4: the canonical product named in the payload, else the brand's product with the same model code, else a new product.</summary>
        private async Task<Result<Product>> ResolveProductAsync(UpsertProductInfo info, CancellationToken cancellationToken)
        {
            if (info.CanonicalProductId is { } canonicalId)
            {
                var canonical = await db.Products.SingleOrDefaultAsync(candidate => candidate.Id == canonicalId, cancellationToken);
                return canonical is null ? CatalogErrors.ProductNotFound : canonical;
            }

            var brandSlug = Slugs.Slugify(info.BrandName);
            var brand = await db.Brands.SingleOrDefaultAsync(candidate => candidate.Slug == brandSlug, cancellationToken);
            var modelCode = NormalizeModelCode(info.ModelCode);

            if (brand is not null && modelCode is not null)
            {
                var sameModel = await db.Products.SingleOrDefaultAsync(
                    candidate => candidate.BrandId == brand.Id && candidate.ModelCode == modelCode, cancellationToken);

                if (sameModel is not null)
                {
                    return sameModel;
                }
            }

            if (!categories.TryGetValue(info.CategorySlug.Trim(), out var categoryId))
            {
                return CatalogErrors.CategoryNotFound;
            }

            if (brand is null)
            {
                brand = new Brand { Name = info.BrandName.Trim(), Slug = brandSlug };
                db.Brands.Add(brand);
            }

            var baseSlug = Slugs.Slugify(info.Name);
            var takenSlugs = await db.Products
                .Where(candidate => candidate.BrandId == brand.Id && candidate.Slug.StartsWith(baseSlug))
                .Select(candidate => candidate.Slug)
                .ToListAsync(cancellationToken);

            var product = new Product
            {
                BrandId = brand.Id,
                CategoryId = categoryId,
                Name = info.Name.Trim(),
                Slug = Slugs.MakeUnique(baseSlug, takenSlugs.ToHashSet(StringComparer.Ordinal)),
                ModelCode = modelCode,
                Audience = Enum.Parse<Audience>(info.Audience, ignoreCase: true),
                PrimaryColor = Enum.Parse<ProductColor>(info.PrimaryColor, ignoreCase: true),
                Pattern = ProductPattern.Other,
                Fit = ProductFit.Unknown,
                Formality = DefaultFormality,
                Seasons = Season.AllYear,
            };
            ApplyMutableAttributes(product, info);
            db.Products.Add(product);

            return product;
        }

        private async Task AddMissingImagesAsync(Product product, IReadOnlyList<string>? imageUrls, CancellationToken cancellationToken)
        {
            var urls = imageUrls?.Select(url => url.Trim()).Distinct(StringComparer.Ordinal).ToList() ?? [];
            if (urls.Count == 0)
            {
                return;
            }

            var isNewProduct = db.Entry(product).State == EntityState.Added;
            var existing = isNewProduct
                ? []
                : await db.ProductImages.Where(image => image.ProductId == product.Id).Select(image => image.Url).ToListAsync(cancellationToken);
            var nextSortOrder = isNewProduct ? 0 : await NextSortOrderAsync(product.Id, cancellationToken);

            foreach (var url in urls.Where(url => !existing.Contains(url)))
            {
                db.ProductImages.Add(new ProductImage { ProductId = product.Id, Url = url, SortOrder = nextSortOrder++ });
                product.PrimaryImageUrl ??= url;
            }
        }

        private async Task<int> NextSortOrderAsync(Guid productId, CancellationToken cancellationToken) =>
            await db.ProductImages.Where(image => image.ProductId == productId).MaxAsync(image => (int?)image.SortOrder, cancellationToken) + 1 ?? 0;
    }

    private static ItemOutcome Fail(IndexedItem entry, Error error) =>
        new(entry.Index, entry.Item.ExternalId.Trim(), error, Created: false, SnapshotWritten: false);

    private static string? NormalizeModelCode(string? modelCode) =>
        string.IsNullOrWhiteSpace(modelCode) ? null : modelCode.Trim().ToUpperInvariant();

    private static Seller CreateBrandStore(string brandName, string itemUrl)
    {
        var name = $"{brandName} Store";

        return new Seller
        {
            Name = name.Length > 100 ? name[..100] : name,
            Slug = Slugs.Slugify(brandName) + BrandStoreSuffix,
            Kind = SellerKind.BrandStore,
            WebsiteUrl = new Uri(itemUrl.Trim()).GetLeftPart(UriPartial.Authority),
            IsActive = true,
        };
    }

    private static void ApplyListing(Offer offer, UpsertOfferItem item, StockStatus stockStatus, DateTimeOffset now)
    {
        offer.Url = item.Url.Trim();
        offer.Title = item.Title.Trim();
        offer.CurrentPrice = item.Price;
        offer.OriginalPrice = item.OriginalPrice;
        offer.Currency = (item.Currency ?? Offer.DefaultCurrency).Trim().ToUpperInvariant();
        offer.ShippingCost = item.ShippingCost;
        offer.FreeShippingThreshold = item.FreeShippingThreshold;
        offer.StockStatus = stockStatus;
        offer.AvailableSizes = [.. item.AvailableSizes?.Select(size => size.Trim()).Distinct(StringComparer.Ordinal) ?? []];
        offer.LastSeenAt = now;
        offer.IsActive = true;
    }

    /// <summary>Optional descriptive attributes only; identity (brand, name, model code, category) never changes. Absent values leave the product as it is.</summary>
    private static void ApplyMutableAttributes(Product product, UpsertProductInfo info)
    {
        if (info.Description is not null)
        {
            product.Description = info.Description.Trim();
        }

        if (info.Pattern is not null)
        {
            product.Pattern = Enum.Parse<ProductPattern>(info.Pattern, ignoreCase: true);
        }

        if (info.Fit is not null)
        {
            product.Fit = Enum.Parse<ProductFit>(info.Fit, ignoreCase: true);
        }

        if (info.Formality is { } formality)
        {
            product.Formality = (short)formality;
        }

        if (info.Seasons is { Count: > 0 })
        {
            product.Seasons = EnumNames.ParseSeasons(info.Seasons);
        }

        if (info.Material is not null)
        {
            product.Material = info.Material.Trim();
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Offer upsert group of {Count} items starting at index {FirstIndex} failed; retrying its items one by one.")]
    private static partial void LogGroupFailed(ILogger logger, Exception exception, int firstIndex, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Offer upsert item {Index} ({ExternalId}) failed on its own and was skipped.")]
    private static partial void LogItemFailed(ILogger logger, Exception exception, int index, string externalId);
}
