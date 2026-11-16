using Kombin.Api.Features.Catalog.Jobs;
using Kombin.Api.Persistence.Entities.Catalog;
using Microsoft.EntityFrameworkCore;
using static Kombin.Api.IntegrationTests.CatalogClientExtensions;

namespace Kombin.Api.IntegrationTests;

/// <summary>
/// Task B5: an upsert of an existing offer refreshes the product's gallery and primary image, under one rule: only the offer that supplied
/// the gallery may replace it (<see cref="Product.ImageSourceOfferId"/>), an empty list never clears it and an identical list writes nothing.
/// </summary>
[Collection(EmbeddingRuns.Name)]
public sealed class CatalogImageRefreshTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Image(string look, string name) => $"https://img.refresh.test/{look}/{name}.jpg";

    private static string Look() => $"look-{Guid.NewGuid():N}"[..17];

    private sealed record Gallery(string? Primary, string[] Urls, Guid[] ImageIds, Guid? Source, DateTimeOffset UpdatedAt);

    private async Task<Gallery> GalleryAsync(Guid productId)
    {
        var product = await factory.WithDbAsync(db => db.Products.AsNoTracking().SingleAsync(candidate => candidate.Id == productId, Ct));
        var images = await factory.WithDbAsync(db => db.ProductImages.AsNoTracking()
            .Where(image => image.ProductId == productId).OrderBy(image => image.SortOrder).ToListAsync(Ct));

        return new Gallery(product.PrimaryImageUrl, [.. images.Select(image => image.Url)], [.. images.Select(image => image.Id)], product.ImageSourceOfferId, product.UpdatedAt);
    }

    private static void AssertSame(Gallery expected, Gallery actual)
    {
        Assert.Equal(expected.Primary, actual.Primary);
        Assert.Equal(expected.Urls, actual.Urls);
        Assert.Equal(expected.ImageIds, actual.ImageIds);
        Assert.Equal(expected.Source, actual.Source);
        Assert.Equal(expected.UpdatedAt, actual.UpdatedAt);
    }

    private static async Task<(Guid ProductId, Guid OfferId)> UpsertAsync(
        HttpClient client, string seller, string externalId, string brand, string modelCode, string[] imageUrls, decimal price = 499m)
    {
        var item = Item(externalId, price, brand, "Parça", modelCode: modelCode, imageUrls: imageUrls, category: "ust-giyim/tisort", color: "Navy");
        var body = await client.UpsertOkAsync(seller, [item], Ct);
        var result = body.GetProperty("results")[0];

        return (result.GetProperty("productId").GetGuid(), result.GetProperty("offerId").GetGuid());
    }

    [Fact]
    public async Task AReorderedGalleryReplacesTheStoredOneAndTheFirstUrlBecomesThePrimaryImage()
    {
        using var client = factory.CreateClient();
        var look = Look();
        var brand = Unique("Refresh");
        var seller = $"{Slug(brand)}-store";
        var externalId = $"ext-{Guid.NewGuid():N}";
        string[] first = [Image(look, "a"), Image(look, "b"), Image(look, "c")];

        var (productId, offerId) = await UpsertAsync(client, seller, externalId, brand, "RF-1", first);
        var before = await GalleryAsync(productId);
        Assert.Equal(first[0], before.Primary);
        Assert.Equal(first, before.Urls);
        Assert.Equal(offerId, before.Source);

        // The worker now sends the person-free shot first, and drops the last one for a new one.
        string[] reordered = [Image(look, "c"), Image(look, "a"), Image(look, "d")];
        await UpsertAsync(client, seller, externalId, brand, "RF-1", reordered);

        var after = await GalleryAsync(productId);
        Assert.Equal(reordered, after.Urls);
        Assert.Equal(reordered[0], after.Primary);
        Assert.Equal(offerId, after.Source);

        // "a" and "c" keep their rows (the embedding row points at an image), only "b" is gone and "d" is new.
        Assert.Equal(before.ImageIds[2], after.ImageIds[0]);
        Assert.Equal(before.ImageIds[0], after.ImageIds[1]);
        Assert.DoesNotContain(before.ImageIds[1], after.ImageIds);
        Assert.Equal(3, after.ImageIds.Length);

        // The product detail the app reads shows the new order.
        var detail = await client.GetJsonAsync($"/api/v1/catalog/products/{productId}", Ct);
        Assert.Equal(reordered[0], detail.GetProperty("primaryImageUrl").GetString());
    }

    [Fact]
    public async Task AnEmptyOrMissingListNeverClearsTheGallery()
    {
        using var client = factory.CreateClient();
        var look = Look();
        var brand = Unique("Refresh");
        var seller = $"{Slug(brand)}-store";
        var externalId = $"ext-{Guid.NewGuid():N}";
        string[] urls = [Image(look, "a"), Image(look, "b")];

        var (productId, _) = await UpsertAsync(client, seller, externalId, brand, "RF-2", urls);
        var before = await GalleryAsync(productId);

        await UpsertAsync(client, seller, externalId, brand, "RF-2", [], price: 450m);

        var after = await GalleryAsync(productId);
        AssertSame(before, after);
    }

    [Fact]
    public async Task AnotherSellersOfferNeitherReplacesNorExtendsTheGalleryAndOnlyTheOwnerCan()
    {
        using var client = factory.CreateClient();
        var look = Look();
        var brand = Unique("Refresh");
        var brandStore = $"{Slug(brand)}-store";
        string[] ownersGallery = [Image(look, "a"), Image(look, "b")];
        string[] otherGallery = [Image(look, "x"), Image(look, "y"), Image(look, "z")];
        var ownerExternalId = $"own-{Guid.NewGuid():N}";
        var otherExternalId = $"oth-{Guid.NewGuid():N}";

        var (productId, ownerOfferId) = await UpsertAsync(client, brandStore, ownerExternalId, brand, "RF-3", ownersGallery);
        var before = await GalleryAsync(productId);

        // Trendyol lists the same model code: a new offer on the same product, with a different gallery.
        var (otherProductId, _) = await UpsertAsync(client, "trendyol", otherExternalId, brand, "RF-3", otherGallery);
        Assert.Equal(productId, otherProductId);
        AssertSame(before, await GalleryAsync(productId));

        // Its next runs do not flip the gallery either, whatever order it sends.
        await UpsertAsync(client, "trendyol", otherExternalId, brand, "RF-3", [.. otherGallery.Reverse()]);
        await UpsertAsync(client, "trendyol", otherExternalId, brand, "RF-3", otherGallery, price: 480m);
        AssertSame(before, await GalleryAsync(productId));

        // The owner can still refresh it.
        string[] refreshed = [Image(look, "b"), Image(look, "a")];
        await UpsertAsync(client, brandStore, ownerExternalId, brand, "RF-3", refreshed);

        var after = await GalleryAsync(productId);
        Assert.Equal(refreshed, after.Urls);
        Assert.Equal(refreshed[0], after.Primary);
        Assert.Equal(ownerOfferId, after.Source);
    }

    [Fact]
    public async Task AGalleryNobodyOwnsYetIsClaimedAndRefreshedByTheNextUpsertOfAnyOfItsOffers()
    {
        using var client = factory.CreateClient();
        var look = Look();
        var brand = Unique("Refresh");
        var brandStore = $"{Slug(brand)}-store";
        var ownerExternalId = $"own-{Guid.NewGuid():N}";
        var otherExternalId = $"oth-{Guid.NewGuid():N}";

        var (productId, _) = await UpsertAsync(client, brandStore, ownerExternalId, brand, "RF-4", [Image(look, "a"), Image(look, "b")]);
        var (_, otherOfferId) = await UpsertAsync(client, "trendyol", otherExternalId, brand, "RF-4", [Image(look, "q")]);

        // A product that existed before the column: no owner.
        await factory.WithDbAsync(db => db.Products.Where(product => product.Id == productId)
            .ExecuteUpdateAsync(set => set.SetProperty(product => product.ImageSourceOfferId, (Guid?)null), Ct));

        string[] refreshed = [Image(look, "b"), Image(look, "a")];
        await UpsertAsync(client, "trendyol", otherExternalId, brand, "RF-4", refreshed);

        var claimed = await GalleryAsync(productId);
        Assert.Equal(refreshed, claimed.Urls);
        Assert.Equal(refreshed[0], claimed.Primary);
        Assert.Equal(otherOfferId, claimed.Source);

        // And now the brand store is the one that may not.
        await UpsertAsync(client, brandStore, ownerExternalId, brand, "RF-4", [Image(look, "a")]);
        AssertSame(claimed, await GalleryAsync(productId));
    }

    [Fact]
    public async Task AnIdenticalGalleryWritesNothingAndIsNotEmbeddedAgainWhileANewPrimaryImageIs()
    {
        using var client = factory.CreateClient();
        var look = Look();
        var brand = Unique("Refresh");
        var seller = $"{Slug(brand)}-store";
        var externalId = $"ext-{Guid.NewGuid():N}";
        string[] urls = [Image(look, "a"), Image(look, "b")];

        var (productId, _) = await UpsertAsync(client, seller, externalId, brand, "RF-5", urls);
        Assert.Equal(1, (await EmbedAsync(productId)).Embedded);
        var embedded = await factory.WithDbAsync(db => db.ProductEmbeddings.AsNoTracking().SingleAsync(row => row.ProductId == productId, Ct));
        var before = await GalleryAsync(productId);

        // Same gallery again, with a new price so the offer itself is written: the product and its images are not.
        await UpsertAsync(client, seller, externalId, brand, "RF-5", urls, price: 450m);

        AssertSame(before, await GalleryAsync(productId));
        Assert.Equal(0, (await EmbedAsync(productId)).Due);
        var unchanged = await factory.WithDbAsync(db => db.ProductEmbeddings.AsNoTracking().SingleAsync(row => row.ProductId == productId, Ct));
        Assert.Equal(embedded.UpdatedAt, unchanged.UpdatedAt);

        // Only the order of the tail changes: the primary image is the same, so there is nothing to embed.
        await UpsertAsync(client, seller, externalId, brand, "RF-5", [urls[0], Image(look, "c"), urls[1]]);
        Assert.Equal(urls[0], (await GalleryAsync(productId)).Primary);
        Assert.Equal(0, (await EmbedAsync(productId)).Due);

        // A new primary image makes the product due again, through the same job as on first create.
        await UpsertAsync(client, seller, externalId, brand, "RF-5", [Image(look, "c"), urls[0]]);
        Assert.Equal(Image(look, "c"), (await GalleryAsync(productId)).Primary);

        var run = await EmbedAsync(productId);
        Assert.Equal((1, 1), (run.Due, run.Embedded));
        var refreshed = await factory.WithDbAsync(db => db.ProductEmbeddings.AsNoTracking().SingleAsync(row => row.ProductId == productId, Ct));
        Assert.Equal(Image(look, "c"), refreshed.SourceImageUrl);
        Assert.Equal(ProductEmbeddingStatus.Ready, refreshed.Status);
    }

    private Task<EmbeddingRunSummary> EmbedAsync(Guid productId) =>
        factory.RunJobAsync<ProductEmbeddingJob, EmbeddingRunSummary>(job => job.EmbedAsync([productId], int.MaxValue, null, Ct));
}
