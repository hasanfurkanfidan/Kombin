using Hangfire;
using Kombin.Api.Common.Time;
using Kombin.Api.Features.Catalog.Jobs;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Catalog;
using Microsoft.AspNetCore.Mvc;

namespace Kombin.Api.Features.Admin.Pages;

internal sealed record SellerRow(
    string Name, string Slug, SellerKind Kind, string Country, bool IsActive, string WebsiteUrl, DateTimeOffset? LastSeen, int Products, int Offers, int ActiveOffers);

internal sealed record EmbeddingProblem(Guid ProductId, string Name, ProductEmbeddingStatus Status, int Attempts, DateTimeOffset? NextAttemptAt, string? FailureReason);

internal sealed record EmbeddingOverview(int ActiveProducts, int Ready, int NotTried, int Failed, int Unusable, IReadOnlyList<EmbeddingProblem> Problems);

internal sealed record ProductMatch(Guid Id, string Name, string Brand, string Slug, bool IsActive);

internal sealed record ProductOfferRow(string Seller, decimal Price, StockStatus Stock, bool IsActive, DateTimeOffset LastSeenAt, string Url);

internal sealed record PricePointRow(DateTimeOffset ObservedAt, string Seller, decimal Price, StockStatus Stock);

internal sealed record ProductDetail(
    Guid Id, string Name, string Brand, string Category, string Slug, bool IsActive, string EmbeddingStatus, IReadOnlyList<ProductOfferRow> Offers, IReadOnlyList<PricePointRow> History);

internal sealed class CatalogModel(KombinDbContext db, IClock clock, IBackgroundJobClient jobs) : AdminPageModel
{
    public const int HistoryLimit = 60;

    private const int MaxMatches = 10;

    private const int MaxProblems = 20;

    public DateTimeOffset Now => clock.UtcNow;

    public IReadOnlyList<SellerRow> Sellers { get; private set; } = [];

    public EmbeddingOverview Embeddings { get; private set; } = new(0, 0, 0, 0, 0, []);

    [BindProperty(SupportsGet = true, Name = "q")]
    public string? Query { get; set; }

    [BindProperty(SupportsGet = true, Name = "product")]
    public Guid? ProductId { get; set; }

    public IReadOnlyList<ProductMatch> Matches { get; private set; } = [];

    public ProductDetail? Product { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadSellersAsync(cancellationToken);
        await LoadEmbeddingsAsync(cancellationToken);
        await LoadProductAsync(cancellationToken);
    }

    /// <summary>Hands the work to the Hangfire server (the same <c>EmbedAsync</c> as the internal endpoint and the nightly run) and records who asked.</summary>
    public async Task<IActionResult> OnPostBackfillAsync(int limit, CancellationToken cancellationToken)
    {
        var clamped = Math.Clamp(limit, 1, ProductEmbeddingJob.MaxBackfillLimit);

        try
        {
            var jobId = jobs.Enqueue<ProductEmbeddingJob>(job => job.BackfillAsync(clamped, CancellationToken.None));
            await AuditAsync("catalog.backfill", "catalog", null, $"limit={clamped}; job={jobId}", cancellationToken);
            SetFlash($"Backfill queued for up to {clamped} products (job {jobId}). Reload in a minute to see the numbers move.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SetFlashError($"The backfill could not be queued: {exception.Message} (is Hangfire:Enabled true and the job storage reachable?)");
        }

        return RedirectToPage();
    }

    private async Task LoadSellersAsync(CancellationToken cancellationToken)
    {
        var stats = (await db.Offers.AsNoTracking()
                .GroupBy(offer => offer.SellerId)
                .Select(group => new
                {
                    SellerId = group.Key,
                    Offers = group.Count(),
                    Active = group.Count(offer => offer.IsActive),
                    Products = group.Select(offer => offer.ProductId).Distinct().Count(),
                    LastSeen = group.Max(offer => offer.LastSeenAt),
                })
                .ToListAsync(cancellationToken))
            .ToDictionary(row => row.SellerId);
        var sellers = await db.Sellers.AsNoTracking().OrderBy(seller => seller.Name).ToListAsync(cancellationToken);

        Sellers =
        [
            .. sellers.Select(seller =>
            {
                var row = stats.GetValueOrDefault(seller.Id);

                return new SellerRow(seller.Name, seller.Slug, seller.Kind, seller.Country, seller.IsActive, seller.WebsiteUrl, row?.LastSeen, row?.Products ?? 0, row?.Offers ?? 0, row?.Active ?? 0);
            }),
        ];
    }

    private async Task LoadEmbeddingsAsync(CancellationToken cancellationToken)
    {
        var active = db.Products.AsNoTracking().Where(product => product.IsActive);
        var activeCount = await active.CountAsync(cancellationToken);
        var byStatus = await active
            .Join(db.ProductEmbeddings, product => product.Id, embedding => embedding.ProductId, (_, embedding) => embedding.Status)
            .GroupBy(status => status)
            .Select(group => new { Status = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);
        int Count(ProductEmbeddingStatus status) => byStatus.FirstOrDefault(row => row.Status == status)?.Count ?? 0;

        var problems = (await active
                .Join(db.ProductEmbeddings.Where(embedding => embedding.Status != ProductEmbeddingStatus.Ready), product => product.Id, embedding => embedding.ProductId, (product, embedding) => new { product.Id, product.Name, embedding.Status, embedding.Attempts, embedding.NextAttemptAt, embedding.FailureReason })
                .OrderBy(row => row.Status)
                .ThenBy(row => row.Name)
                .Take(MaxProblems)
                .ToListAsync(cancellationToken))
            .Select(row => new EmbeddingProblem(row.Id, row.Name, row.Status, row.Attempts, row.NextAttemptAt, row.FailureReason))
            .ToList();

        var ready = Count(ProductEmbeddingStatus.Ready);
        var failed = Count(ProductEmbeddingStatus.Failed);
        var unusable = Count(ProductEmbeddingStatus.Unusable);

        Embeddings = new EmbeddingOverview(activeCount, ready, activeCount - ready - failed - unusable, failed, unusable, problems);
    }

    private async Task LoadProductAsync(CancellationToken cancellationToken)
    {
        var query = Query?.Trim();

        if (ProductId is null && !string.IsNullOrEmpty(query))
        {
            var asId = Guid.TryParse(query, out var id) ? id : (Guid?)null;
            var lowered = query.ToLowerInvariant();

            // A slug is unique only within its brand, so "blazer" can match many products: "brand-slug/blazer" narrows it to one.
            var parts = lowered.Split('/', 2);
            var brandSlug = parts.Length == 2 ? parts[0] : null;
            var slug = parts.Length == 2 ? parts[1] : lowered;

            Matches = await db.Products.AsNoTracking()
                .Where(product => (asId != null && product.Id == asId) || (product.Slug == slug && (brandSlug == null || product.Brand.Slug == brandSlug)))
                .OrderBy(product => product.Brand.Name)
                .ThenBy(product => product.Name)
                .Take(MaxMatches)
                .Select(product => new ProductMatch(product.Id, product.Name, product.Brand.Name, product.Slug, product.IsActive))
                .ToListAsync(cancellationToken);

            if (Matches.Count == 1)
            {
                ProductId = Matches[0].Id;
                Matches = [];
            }
        }

        if (ProductId is not { } productId)
        {
            return;
        }

        var found = await db.Products.AsNoTracking()
            .Where(product => product.Id == productId)
            .Select(product => new { product.Id, product.Name, Brand = product.Brand.Name, Category = product.Category.Name, product.Slug, product.IsActive })
            .SingleOrDefaultAsync(cancellationToken);

        if (found is null)
        {
            return;
        }

        var offers = await db.Offers.AsNoTracking()
            .Where(offer => offer.ProductId == productId)
            .OrderByDescending(offer => offer.IsActive)
            .ThenBy(offer => offer.CurrentPrice)
            .Select(offer => new ProductOfferRow(offer.Seller.Name, offer.CurrentPrice, offer.StockStatus, offer.IsActive, offer.LastSeenAt, offer.Url))
            .ToListAsync(cancellationToken);
        var history = await db.PriceSnapshots.AsNoTracking()
            .Where(snapshot => snapshot.Offer.ProductId == productId)
            .OrderByDescending(snapshot => snapshot.ObservedAt)
            .ThenByDescending(snapshot => snapshot.Id)
            .Take(HistoryLimit)
            .Select(snapshot => new PricePointRow(snapshot.ObservedAt, snapshot.Offer.Seller.Name, snapshot.Price, snapshot.StockStatus))
            .ToListAsync(cancellationToken);
        var embedding = await db.ProductEmbeddings.AsNoTracking().Where(row => row.ProductId == productId).Select(row => row.Status.ToString()).SingleOrDefaultAsync(cancellationToken);

        Product = new ProductDetail(found.Id, found.Name, found.Brand, found.Category, found.Slug, found.IsActive, embedding ?? "none yet", offers, history);
    }
}
