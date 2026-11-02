using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kombin.Api.Features.Wardrobe.Jobs;
using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Wardrobe;
using Microsoft.EntityFrameworkCore;
using static Kombin.Api.IntegrationTests.CatalogClientExtensions;

namespace Kombin.Api.IntegrationTests;

internal static class WardrobeClientExtensions
{
    public const string Base = "/api/v1/me/wardrobe";

    public static Task<HttpResponseMessage> CreateUploadUrlRawAsync(this HttpClient client, string? contentType = "image/jpeg", long sizeBytes = 1024, CancellationToken cancellationToken = default) =>
        client.PostAsJsonAsync($"{Base}/items/upload-url", new { contentType, sizeBytes }, cancellationToken);

    /// <summary>Asks for an upload URL, asserts the 201 and returns the body.</summary>
    public static async Task<JsonElement> CreateUploadUrlAsync(this HttpClient client, string contentType = "image/jpeg", CancellationToken cancellationToken = default)
    {
        using var response = await client.CreateUploadUrlRawAsync(contentType, cancellationToken: cancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(cancellationToken));

        return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
    }

    public static Task<HttpResponseMessage> ProcessAsync(this HttpClient client, Guid itemId, CancellationToken cancellationToken = default) =>
        client.PostAsync($"{Base}/items/{itemId}/process", null, cancellationToken);

    public static Task<HttpResponseMessage> PatchItemAsync(this HttpClient client, Guid itemId, object body, CancellationToken cancellationToken = default) =>
        client.PatchAsJsonAsync($"{Base}/items/{itemId}", body, cancellationToken);

    public static Task<HttpResponseMessage> WearAsync(this HttpClient client, Guid itemId, DateOnly? wornOn = null, CancellationToken cancellationToken = default) =>
        wornOn is null
            ? client.PostAsync($"{Base}/items/{itemId}/wear", null, cancellationToken)
            : client.PostAsJsonAsync($"{Base}/items/{itemId}/wear", new { wornOn }, cancellationToken);

    public static Task<HttpResponseMessage> FromProductAsync(this HttpClient client, Guid productId, string? size = null, CancellationToken cancellationToken = default) =>
        client.PostAsJsonAsync($"{Base}/items/from-product", new { productId, size }, cancellationToken);

    public static Task<JsonElement> GetItemAsync(this HttpClient client, Guid itemId, CancellationToken cancellationToken = default) =>
        client.GetJsonAsync($"{Base}/items/{itemId}", cancellationToken);

    public static Task<AnalyzeOutcome> AnalyzeAsync(this ApiFactory factory, Guid itemId, CancellationToken cancellationToken) =>
        factory.RunJobAsync<WardrobeAnalyzeJob, AnalyzeOutcome>(job => job.AnalyzeAsync(itemId, cancellationToken));

    public static Task<CleanupSummary> CleanupWardrobeAsync(this ApiFactory factory, Guid userId, CancellationToken cancellationToken) =>
        factory.RunJobAsync<WardrobeCleanupJob, CleanupSummary>(job => job.CleanupAsync(userId, cancellationToken));

    /// <summary>The full photo path as the app walks it: upload URL, PUT (into the fake store), process, then the job. Returns the item ID.</summary>
    public static async Task<Guid> AddPhotoItemAsync(this ApiFactory factory, HttpClient client, string flavor = "ok", CancellationToken cancellationToken = default)
    {
        var upload = await client.CreateUploadUrlAsync(cancellationToken: cancellationToken);
        var itemId = upload.GetProperty("itemId").GetGuid();
        factory.Storage.Upload(await factory.OriginalKeyAsync(itemId, cancellationToken), FakeVisionClient.Photo(flavor), "image/jpeg");

        using var process = await client.ProcessAsync(itemId, cancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, process.StatusCode);
        await factory.AnalyzeAsync(itemId, cancellationToken);

        return itemId;
    }

    public static Task<string> OriginalKeyAsync(this ApiFactory factory, Guid itemId, CancellationToken cancellationToken) =>
        factory.WithDbAsync(db => db.WardrobeItems.Where(item => item.Id == itemId).Select(item => item.ImageOriginalKey!).SingleAsync(cancellationToken));

    public static Task<WardrobeItem> ItemRowAsync(this ApiFactory factory, Guid itemId, CancellationToken cancellationToken) =>
        factory.WithDbAsync(db => db.WardrobeItems.AsNoTracking().SingleAsync(item => item.Id == itemId, cancellationToken));

    /// <summary>Inserts a ready item directly, for stats and gap tests that need a wardrobe of a given shape.</summary>
    public static Task<Guid> InsertItemAsync(
        this ApiFactory factory, Guid userId, string categorySlug, ProductColor color, short formality = 2, CancellationToken cancellationToken = default) =>
        factory.WithDbAsync(async db =>
        {
            var categoryId = await db.Categories.Where(category => category.Slug == categorySlug).Select(category => category.Id).SingleAsync(cancellationToken);
            var item = new WardrobeItem
            {
                UserId = userId,
                CategoryId = categoryId,
                PrimaryColor = color,
                Formality = formality,
                Status = WardrobeItemStatus.Ready,
            };

            db.WardrobeItems.Add(item);
            await db.SaveChangesAsync(cancellationToken);

            return item.Id;
        });

    /// <summary>Upserts one product with the given traits through a fresh brand store and returns its ID.</summary>
    public static async Task<Guid> SeedCatalogProductAsync(
        this HttpClient client,
        string category,
        string color,
        int formality,
        string? imageUrl = null,
        CancellationToken cancellationToken = default)
    {
        var brand = Unique("Wrd");
        var externalId = $"wrd-{Guid.NewGuid():N}";
        var item = new
        {
            externalId,
            url = $"https://shop.example.com/p/{externalId}",
            title = $"{brand} Parça",
            price = 499m,
            stockStatus = "InStock",
            availableSizes = new[] { "S", "M" },
            imageUrls = new[] { imageUrl ?? $"https://img.example.com/{externalId}.jpg" },
            product = new { brandName = brand, name = "Parça", categorySlug = category, audience = "Unisex", primaryColor = color, formality },
        };

        var body = await client.UpsertOkAsync($"{Slug(brand)}-store", [item], cancellationToken);

        return body.GetProperty("results")[0].GetProperty("productId").GetGuid();
    }
}
