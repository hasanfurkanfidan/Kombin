using System.Net;
using Kombin.Api.Features.Catalog.Jobs;
using Kombin.Api.Persistence.Entities.Catalog;
using Microsoft.EntityFrameworkCore;
using static Kombin.Api.IntegrationTests.CatalogClientExtensions;

namespace Kombin.Api.IntegrationTests;

/// <summary>
/// The offer sweep against a real database. Every run is restricted to one seller: the tests share a database and run in parallel,
/// and the unfiltered sweep would retire other tests' listings. The decisions themselves are covered in <c>OfferSweepRulesTests</c>.
/// </summary>
public sealed class OfferSweepTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<List<PriceSnapshot>> SnapshotsOfAsync(string sellerSlug, string externalId) =>
        factory.WithDbAsync(db => db.PriceSnapshots
            .AsNoTracking()
            .Where(snapshot => snapshot.Offer.Seller.Slug == sellerSlug && snapshot.Offer.ExternalId == externalId)
            .OrderBy(snapshot => snapshot.Id)
            .ToListAsync(Ct));

    private Task<Offer> OfferAsync(string sellerSlug, string externalId) =>
        factory.WithDbAsync(db => db.Offers.AsNoTracking().SingleAsync(offer => offer.Seller.Slug == sellerSlug && offer.ExternalId == externalId, Ct));

    private Task<bool> ProductIsActiveAsync(Guid productId) =>
        factory.WithDbAsync(db => db.Products.AsNoTracking().Where(product => product.Id == productId).Select(product => product.IsActive).SingleAsync(Ct));

    [Fact]
    public async Task AnOfferNotSeenForThreeDaysIsDeactivatedAndAFreshOneIsLeftAlone()
    {
        using var client = factory.CreateClient();
        var (sellerSlug, sellerId) = await client.CreateSellerAsync("Sweep", Ct);
        var brand = Unique("Sweep");
        var gone = $"gone-{Guid.NewGuid():N}";
        var fresh = $"fresh-{Guid.NewGuid():N}";

        await client.UpsertOkAsync(sellerSlug, [Item(gone, 300m, brand, "Gone Blazer"), Item(fresh, 400m, brand, "Fresh Blazer")], Ct);
        await factory.AgeOfferAsync(sellerSlug, gone, TimeSpan.FromDays(3), Ct);

        var summary = await factory.SweepSellerAsync(sellerId, Ct);

        Assert.Equal(1, summary.OffersDeactivated);

        var goneOffer = await OfferAsync(sellerSlug, gone);
        Assert.False(goneOffer.IsActive);
        Assert.Equal(StockStatus.OutOfStock, goneOffer.StockStatus);
        Assert.Equal(300m, goneOffer.CurrentPrice);

        var freshOffer = await OfferAsync(sellerSlug, fresh);
        Assert.True(freshOffer.IsActive);
        Assert.Equal(StockStatus.InStock, freshOffer.StockStatus);
    }

    [Fact]
    public async Task ADeactivatedOfferGetsAClosingOutOfStockSnapshotSoThePriceHistoryShowsTheGap()
    {
        using var client = factory.CreateClient();
        var (sellerSlug, sellerId) = await client.CreateSellerAsync("Gap", Ct);
        var brand = Unique("Gap");
        var externalId = $"gap-{Guid.NewGuid():N}";

        await client.UpsertOkAsync(sellerSlug, [Item(externalId, 250m, brand, "Gap Coat", originalPrice: 300m)], Ct);
        await factory.AgeOfferAsync(sellerSlug, externalId, TimeSpan.FromDays(3), Ct);

        await factory.SweepSellerAsync(sellerId, Ct);

        var snapshots = await SnapshotsOfAsync(sellerSlug, externalId);
        Assert.Equal(2, snapshots.Count);
        Assert.Equal(StockStatus.InStock, snapshots[0].StockStatus);

        var closing = snapshots[1];
        Assert.Equal(StockStatus.OutOfStock, closing.StockStatus);
        Assert.Equal(250m, closing.Price);
        Assert.Equal(300m, closing.OriginalPrice);
        Assert.True(closing.ObservedAt > snapshots[0].ObservedAt);
    }

    [Fact]
    public async Task ASecondSweepFindsNothingLeftToDo()
    {
        using var client = factory.CreateClient();
        var (sellerSlug, sellerId) = await client.CreateSellerAsync("Twice", Ct);
        var externalId = $"twice-{Guid.NewGuid():N}";

        await client.UpsertOkAsync(sellerSlug, [Item(externalId, 100m, Unique("Twice"), "Twice Shirt")], Ct);
        await factory.AgeOfferAsync(sellerSlug, externalId, TimeSpan.FromDays(3), Ct);

        var first = await factory.SweepSellerAsync(sellerId, Ct);
        var second = await factory.SweepSellerAsync(sellerId, Ct);

        Assert.Equal(new(1, 1, 0), first);
        Assert.Equal(new(0, 0, 0), second);
        Assert.Equal(2, (await SnapshotsOfAsync(sellerSlug, externalId)).Count);
    }

    [Fact]
    public async Task AProductWhoseLastOfferIsSweptBecomesInactiveAndDisappearsFromTheList()
    {
        using var client = factory.CreateClient();
        var (sellerSlug, sellerId) = await client.CreateSellerAsync("Vanish", Ct);
        var brand = Unique("Vanish");
        var externalId = $"vanish-{Guid.NewGuid():N}";

        await client.UpsertOkAsync(sellerSlug, [Item(externalId, 100m, brand, "Vanishing Jacket")], Ct);
        var productId = await client.FindProductIdAsync(Slug(brand), Ct);
        Assert.Equal(1, (await client.GetJsonAsync($"/api/v1/catalog/products?brandSlug={Slug(brand)}", Ct)).GetProperty("totalCount").GetInt32());

        await factory.AgeOfferAsync(sellerSlug, externalId, TimeSpan.FromDays(3), Ct);
        var summary = await factory.SweepSellerAsync(sellerId, Ct);

        Assert.Equal(1, summary.ProductsDeactivated);
        Assert.False(await ProductIsActiveAsync(productId));
        Assert.Equal(0, (await client.GetJsonAsync($"/api/v1/catalog/products?brandSlug={Slug(brand)}", Ct)).GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task AProductKeepsLivingWhileAnotherSellerStillHasAFreshOfferForIt()
    {
        using var client = factory.CreateClient();
        var (staleSeller, staleSellerId) = await client.CreateSellerAsync("Stale", Ct);
        var (freshSeller, _) = await client.CreateSellerAsync("Fresh", Ct);
        var brand = Unique("Shared");
        var staleId = $"s-{Guid.NewGuid():N}";
        var freshId = $"f-{Guid.NewGuid():N}";

        await client.UpsertOkAsync(staleSeller, [Item(staleId, 100m, brand, "Shared Parka", modelCode: "SH-1")], Ct);
        await client.UpsertOkAsync(freshSeller, [Item(freshId, 110m, brand, "Shared Parka", modelCode: "SH-1")], Ct);
        var productId = await client.FindProductIdAsync(Slug(brand), Ct);
        await factory.AgeOfferAsync(staleSeller, staleId, TimeSpan.FromDays(3), Ct);

        var summary = await factory.SweepSellerAsync(staleSellerId, Ct);

        Assert.Equal(new(1, 0, 0), summary);
        Assert.True(await ProductIsActiveAsync(productId));

        var detail = await client.GetJsonAsync($"/api/v1/catalog/products/{productId}", Ct);
        Assert.Equal(freshSeller, Assert.Single(detail.GetProperty("offers").EnumerateArray()).GetProperty("seller").GetProperty("slug").GetString());
    }

    [Fact]
    public async Task ProductActivityIsRepairedInBothDirections()
    {
        using var client = factory.CreateClient();
        var (sellerSlug, sellerId) = await client.CreateSellerAsync("Repair", Ct);
        var brand = Unique("Repair");
        var shouldBeOn = $"on-{Guid.NewGuid():N}";
        var shouldBeOff = $"off-{Guid.NewGuid():N}";

        await client.UpsertOkAsync(sellerSlug, [Item(shouldBeOn, 100m, brand, "Wrongly Off"), Item(shouldBeOff, 100m, brand, "Wrongly On")], Ct);
        var onProduct = (await OfferAsync(sellerSlug, shouldBeOn)).ProductId;
        var offProduct = (await OfferAsync(sellerSlug, shouldBeOff)).ProductId;

        // A product flagged inactive although it has an active offer, and one flagged active although its only offer is gone.
        await factory.WithDbAsync(async db =>
        {
            await db.Products.Where(product => product.Id == onProduct).ExecuteUpdateAsync(setters => setters.SetProperty(product => product.IsActive, false), Ct);
            return await db.Offers.Where(offer => offer.ProductId == offProduct).ExecuteUpdateAsync(setters => setters.SetProperty(offer => offer.IsActive, false), Ct);
        });

        var summary = await factory.SweepSellerAsync(sellerId, Ct);

        Assert.Equal(new(0, 1, 1), summary);
        Assert.True(await ProductIsActiveAsync(onProduct));
        Assert.False(await ProductIsActiveAsync(offProduct));
    }

    [Fact]
    public async Task ASeriesOfBatchesIsSweptToTheEnd()
    {
        using var client = factory.CreateClient();
        var (sellerSlug, sellerId) = await client.CreateSellerAsync("Batches", Ct);
        var brand = Unique("Batches");
        var count = OfferSweepJob.BatchSize + 1;

        var items = Enumerable.Range(0, count).Select(index => Item($"b{index}-{Guid.NewGuid():N}", 10m + index, brand, $"Batch Thing {index}")).ToArray();
        foreach (var chunk in items.Chunk(250))
        {
            await client.UpsertOkAsync(sellerSlug, chunk, Ct);
        }

        await factory.WithDbAsync(db => db.Offers
            .Where(offer => offer.Seller.Slug == sellerSlug)
            .ExecuteUpdateAsync(setters => setters.SetProperty(offer => offer.LastSeenAt, DateTimeOffset.UtcNow - TimeSpan.FromDays(3)), Ct));

        var summary = await factory.SweepSellerAsync(sellerId, Ct);

        Assert.Equal(new(count, count, 0), summary);
        Assert.Equal(0, await factory.WithDbAsync(db => db.Offers.CountAsync(offer => offer.Seller.Slug == sellerSlug && offer.IsActive, Ct)));
        Assert.Equal(0, await factory.WithDbAsync(db => db.Products.CountAsync(product => product.Brand.Slug == Slug(brand) && product.IsActive, Ct)));
    }

    [Fact]
    public async Task ASweptOfferThatReturnsInStockFiresBackInStockButSweepingItselfFiresNothing()
    {
        using var user = await factory.CreateUserAsync(Ct);
        using var client = factory.CreateClient();
        var (sellerSlug, sellerId) = await client.CreateSellerAsync("Restock", Ct);
        var brand = Unique("Restock");
        var externalId = $"re-{Guid.NewGuid():N}";

        await client.UpsertOkAsync(sellerSlug, [Item(externalId, 500m, brand, "Restock Blazer")], Ct);
        var productId = await client.FindProductIdAsync(Slug(brand), Ct);
        await user.Client.CreateAlertOkAsync(productId, "BackInStock", cancellationToken: Ct);
        await user.Client.CreateAlertOkAsync(productId, "PriceDrop", 450m, cancellationToken: Ct);
        await user.Client.CreateAlertOkAsync(productId, "AnyChange", cancellationToken: Ct);
        await factory.AgeOfferAsync(sellerSlug, externalId, TimeSpan.FromDays(3), Ct);

        await factory.SweepSellerAsync(sellerId, Ct);

        // No alert fires for the sweep's own deactivation, "any change" included, although the stock did change.
        Assert.Equal(0, await factory.WithDbAsync(db => db.AlertTriggers.CountAsync(trigger => trigger.UserId == user.Id, Ct)));

        // The worker finds the listing again, in stock and cheaper: it is a restock from the sweep's out-of-stock state.
        await client.UpsertOkAsync(sellerSlug, [Item(externalId, 400m, brand, "Restock Blazer")], Ct);

        var kinds = await factory.WithDbAsync(db => db.AlertTriggers.Where(trigger => trigger.UserId == user.Id).Select(trigger => trigger.Kind).ToListAsync(Ct));
        Assert.Equal(["AnyChange", "BackInStock", "PriceDrop"], kinds.Select(kind => kind.ToString()).Order());
    }

    [Fact]
    public async Task TheEndpointSweepsOnlyTheNamedSellerAndReturnsTheCounts()
    {
        using var client = factory.CreateClient();
        var (named, _) = await client.CreateSellerAsync("Named", Ct);
        var (other, _) = await client.CreateSellerAsync("Other", Ct);
        var namedId = $"n-{Guid.NewGuid():N}";
        var otherId = $"o-{Guid.NewGuid():N}";

        await client.UpsertOkAsync(named, [Item(namedId, 100m, Unique("Named"), "Named Shirt")], Ct);
        await client.UpsertOkAsync(other, [Item(otherId, 100m, Unique("Other"), "Other Shirt")], Ct);
        await factory.AgeOfferAsync(named, namedId, TimeSpan.FromDays(3), Ct);
        await factory.AgeOfferAsync(other, otherId, TimeSpan.FromDays(3), Ct);

        var counts = await client.SweepSellerOkAsync(named, Ct);

        Assert.Equal(1, counts.GetProperty("offersDeactivated").GetInt32());
        Assert.Equal(1, counts.GetProperty("productsDeactivated").GetInt32());
        Assert.Equal(0, counts.GetProperty("productsReactivated").GetInt32());
        Assert.False((await OfferAsync(named, namedId)).IsActive);
        Assert.True((await OfferAsync(other, otherId)).IsActive);

        // Idempotent through the endpoint too.
        var again = await client.SweepSellerOkAsync(named, Ct);
        Assert.Equal(0, again.GetProperty("offersDeactivated").GetInt32());
    }

    [Fact]
    public async Task TheEndpointRejectsAnUnknownSeller()
    {
        using var client = factory.CreateClient();

        using var response = await client.SweepAsync(new { sellerSlug = "no-such-seller" }, cancellationToken: Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("Catalog.SellerNotFound", (await response.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task TheEndpointNeedsTheInternalKey()
    {
        using var client = factory.CreateClient();

        using var withoutKey = await client.SweepAsync(new { sellerSlug = "trendyol" }, internalKey: null, cancellationToken: Ct);
        using var wrongKey = await client.SweepAsync(new { sellerSlug = "trendyol" }, internalKey: "not-the-key", cancellationToken: Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, withoutKey.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongKey.StatusCode);
        Assert.Equal("Catalog.InvalidInternalKey", (await withoutKey.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString());
    }
}
