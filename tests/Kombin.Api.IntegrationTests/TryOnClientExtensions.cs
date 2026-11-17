using System.Text;
using System.Text.Json;
using Kombin.Api.Common.Storage;
using Kombin.Api.Features.TryOn.Jobs;
using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Recommendation;
using Kombin.Api.Persistence.Entities.Wardrobe;
using Microsoft.EntityFrameworkCore;

namespace Kombin.Api.IntegrationTests;

/// <summary>
/// The classes that run <c>tryon-prepare</c> against the shared database. Catalogue prepares take one advisory lock per database
/// (<c>TryOnCatalogueLock</c>), so two of these classes in parallel would skip each other's catalogue work; this collection runs them one after
/// another, and no other class starts a run.
/// </summary>
[CollectionDefinition(Name)]
public sealed class TryOnJobs
{
    public const string Name = "Try-on jobs";
}

/// <summary>A piece of a seeded outfit.</summary>
internal sealed record SeedPiece(OutfitSlot Slot, Guid? WardrobeItemId = null, Guid? ProductId = null);

/// <summary>Try-on test data: wardrobe pieces, products, outfits and the job, all built directly (the fake vision answers by the photos' text).</summary>
internal static class TryOnClientExtensions
{
    /// <summary>12:00 Istanbul on a weekday: daytime for the catalogue batch.</summary>
    public static readonly DateTimeOffset Noon = new(2026, 11, 18, 9, 0, 0, TimeSpan.Zero);

    /// <summary>03:00 Istanbul: night for the catalogue batch.</summary>
    public static readonly DateTimeOffset Night = new(2026, 11, 18, 0, 0, 0, TimeSpan.Zero);

    public static Task<TryOnPrepareSummary> RunTryOnAsync(
        this ApiFactory factory, DateTimeOffset now, IReadOnlyCollection<Guid>? items = null, IReadOnlyCollection<Guid>? products = null, IReadOnlyCollection<Guid>? users = null) =>
        factory.RunJobAsync<TryOnPrepareJob, TryOnPrepareSummary>(job => job.ProcessAsync(now, new TryOnScope(items ?? [], products ?? [], users ?? []), CancellationToken.None));

    public static Task<(Guid Id, string Slug)> SeedBrandAsync(this ApiFactory factory) =>
        factory.WithDbAsync(async db =>
        {
            var name = CatalogClientExtensions.Unique("TryOn");
            var brand = new Brand { Name = name, Slug = name.ToLowerInvariant() };
            db.Brands.Add(brand);
            await db.SaveChangesAsync();

            return (brand.Id, brand.Slug);
        });

    /// <summary>A ready wardrobe item whose stored cutout says <paramref name="text"/> (put a test marker and try-on flavours in it).</summary>
    public static Task<Guid> SeedTryOnItemAsync(
        this ApiFactory factory,
        Guid userId,
        string categorySlug,
        string text,
        string? size = null,
        Guid? brandId = null,
        string[]? warnings = null,
        ProductColor color = ProductColor.Black) =>
        factory.WithDbAsync(async db =>
        {
            var item = new WardrobeItem
            {
                UserId = userId,
                CategoryId = await db.Categories.Where(category => category.Slug == categorySlug).Select(category => category.Id).SingleAsync(),
                PrimaryColor = color,
                Formality = 2,
                Fit = ProductFit.Regular,
                Size = size,
                BrandId = brandId,
                Warnings = warnings ?? [],
                Status = WardrobeItemStatus.Ready,
            };
            item.ImageCutoutKey = StorageKeys.Build("wardrobe", userId, item.Id, ImageVariant.Cutout, "png");
            factory.Storage.Upload(item.ImageCutoutKey, Encoding.UTF8.GetBytes($"cutout-png-bytes:{text}"), "image/png");
            db.WardrobeItems.Add(item);
            await db.SaveChangesAsync();

            return item.Id;
        });

    /// <summary>An active product with the given gallery (the image texts are the URLs: put a marker and flavours in them).</summary>
    public static Task<Guid> SeedTryOnProductAsync(
        this ApiFactory factory, string categorySlug, IReadOnlyList<string> imageUrls, Guid? brandId = null, ProductColor color = ProductColor.White) =>
        factory.WithDbAsync(async db =>
        {
            var brand = brandId ?? (await factory.SeedBrandAsync()).Id;
            var name = CatalogClientExtensions.Unique("Parca");
            var product = new Product
            {
                BrandId = brand,
                CategoryId = await db.Categories.Where(category => category.Slug == categorySlug).Select(category => category.Id).SingleAsync(),
                Name = name,
                Slug = name.ToLowerInvariant(),
                Audience = Audience.Unisex,
                PrimaryColor = color,
                Pattern = ProductPattern.Solid,
                Fit = ProductFit.Regular,
                Formality = 2,
                Seasons = Season.AllYear,
                PrimaryImageUrl = imageUrls.Count > 0 ? imageUrls[0] : null,
            };
            db.Products.Add(product);
            for (var index = 0; index < imageUrls.Count; index++)
            {
                db.ProductImages.Add(new ProductImage { ProductId = product.Id, Url = imageUrls[index], SortOrder = index });
            }

            await db.SaveChangesAsync();

            return product.Id;
        });

    public static string ImageUrl(string marker, string flavor = "ok", int index = 0) => $"https://img.example.com/tryon/{marker}/{flavor}/{index}.png";

    public static Task<Guid> SeedOutfitAsync(this ApiFactory factory, Guid userId, params SeedPiece[] pieces) =>
        factory.WithDbAsync(async db =>
        {
            var outfit = new Outfit
            {
                UserId = userId,
                Kind = OutfitKind.Daily,
                Completeness = pieces.All(piece => piece.WardrobeItemId is not null) ? OutfitCompleteness.Complete : OutfitCompleteness.NeedsOne,
                ForDate = DateOnly.FromDateTime(DateTime.UtcNow),
                Score = 80,
                Rule = "Neutral",
                Hash = Guid.NewGuid().ToString("N"),
                Status = OutfitStatus.Saved,
                NarrationInput = "{}",
            };
            for (var index = 0; index < pieces.Length; index++)
            {
                outfit.Items.Add(new OutfitItem
                {
                    Slot = pieces[index].Slot,
                    Role = pieces[index].WardrobeItemId is null ? OutfitItemRole.Gap : OutfitItemRole.Wardrobe,
                    WardrobeItemId = pieces[index].WardrobeItemId,
                    ProductId = pieces[index].ProductId,
                    Position = index,
                });
            }

            db.Outfits.Add(outfit);
            await db.SaveChangesAsync();

            return outfit.Id;
        });

    /// <summary>A top, trousers and shoes of the user, each cutout carrying <paramref name="marker"/> (and the top <paramref name="topFlavor"/>), in an outfit.</summary>
    public static async Task<(Guid OutfitId, Guid Top, Guid Trousers, Guid Shoes)> SeedWardrobeOutfitAsync(
        this ApiFactory factory, Guid userId, string marker, string topFlavor = "", string? topSize = null, Guid? brandId = null)
    {
        var top = await factory.SeedTryOnItemAsync(userId, "ust-giyim/gomlek", $"{marker}:top:{topFlavor}", topSize, brandId);
        var trousers = await factory.SeedTryOnItemAsync(userId, "alt-giyim/pantolon", $"{marker}:trousers");
        var shoes = await factory.SeedTryOnItemAsync(userId, "ayakkabi/sneaker", $"{marker}:shoes");
        var outfit = await factory.SeedOutfitAsync(userId, new SeedPiece(OutfitSlot.Top, top), new SeedPiece(OutfitSlot.Bottom, trousers), new SeedPiece(OutfitSlot.Shoes, shoes));

        return (outfit, top, trousers, shoes);
    }

    public static async Task<JsonElement> GetTryOnOkAsync(this HttpClient client, string query, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync($"/api/v1/me/tryon?{query}", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");

        return JsonDocument.Parse(body).RootElement.Clone();
    }

    public static string Status(this JsonElement answer) => answer.GetProperty("status").GetString()!;

    public static string? Reason(this JsonElement answer) => answer.GetProperty("reason").ValueKind == JsonValueKind.Null ? null : answer.GetProperty("reason").GetString();
}
