using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Wardrobe;
using Microsoft.EntityFrameworkCore;
using static Kombin.Api.IntegrationTests.CatalogClientExtensions;

namespace Kombin.Api.IntegrationTests;

/// <summary>A wardrobe piece to insert directly (ready, confirmed), with the traits the outfit engine reads.</summary>
internal sealed record WardrobeSeed(
    string Name,
    string CategorySlug,
    ProductColor Color,
    short Formality,
    ProductPattern Pattern = ProductPattern.Solid,
    ProductFit Fit = ProductFit.Regular,
    Season Seasons = Season.AllYear);

internal static class RecommendationClientExtensions
{
    public const string Base = "/api/v1/me/outfits";

    private static readonly string[] ComparedSlots = ["Top", "Bottom", "Shoes", "Outerwear"];

    private static readonly string[] CoreSlots = ["Top", "Bottom", "Shoes"];

    /// <summary>
    /// A realistic 40-piece wardrobe: 14 tops, 10 bottoms, 7 shoes, 6 layers (two blazers among them), 3 accessories, mostly neutrals
    /// with a few colours and patterns. Fixed, so every run builds the same outfits.
    /// </summary>
    public static readonly IReadOnlyList<WardrobeSeed> FortyPieces =
    [
        new("white tee", "ust-giyim/tisort", ProductColor.White, 2),
        new("black tee", "ust-giyim/tisort", ProductColor.Black, 2),
        new("grey tee", "ust-giyim/tisort", ProductColor.Grey, 2),
        new("breton tee", "ust-giyim/tisort", ProductColor.Navy, 2, ProductPattern.Striped, ProductFit.Slim),
        new("green tee", "ust-giyim/tisort", ProductColor.Green, 2),
        new("printed tee", "ust-giyim/tisort", ProductColor.Multi, 1, ProductPattern.Printed, ProductFit.Oversized),
        new("red tee", "ust-giyim/tisort", ProductColor.Red, 2, Fit: ProductFit.Oversized),
        new("white shirt", "ust-giyim/gomlek", ProductColor.White, 3),
        new("blue shirt", "ust-giyim/gomlek", ProductColor.Blue, 3, Fit: ProductFit.Slim),
        new("dress shirt", "ust-giyim/gomlek", ProductColor.White, 4, Fit: ProductFit.Slim),
        new("gingham shirt", "ust-giyim/gomlek", ProductColor.Beige, 3, ProductPattern.Checked),
        new("grey knit", "ust-giyim/triko", ProductColor.Grey, 3, ProductPattern.Textured, Seasons: Season.Autumn | Season.Winter),
        new("beige knit", "ust-giyim/triko", ProductColor.Beige, 3, Fit: ProductFit.Oversized, Seasons: Season.Autumn | Season.Winter),
        new("navy knit", "ust-giyim/triko", ProductColor.Navy, 3, Seasons: Season.Autumn | Season.Winter),
        new("blue jeans", "alt-giyim/jean", ProductColor.Blue, 2, Fit: ProductFit.Slim),
        new("black jeans", "alt-giyim/jean", ProductColor.Black, 2, Fit: ProductFit.Slim),
        new("wide jeans", "alt-giyim/jean", ProductColor.Blue, 2, Fit: ProductFit.Relaxed),
        new("navy chinos", "alt-giyim/pantolon", ProductColor.Navy, 3),
        new("beige chinos", "alt-giyim/pantolon", ProductColor.Beige, 3),
        new("grey trousers", "alt-giyim/pantolon", ProductColor.Grey, 4, Fit: ProductFit.Slim),
        new("black trousers", "alt-giyim/pantolon", ProductColor.Black, 4),
        new("brown cords", "alt-giyim/pantolon", ProductColor.Brown, 3, ProductPattern.Textured, ProductFit.Relaxed),
        new("black skirt", "alt-giyim/etek", ProductColor.Black, 3),
        new("red skirt", "alt-giyim/etek", ProductColor.Red, 2),
        new("white sneakers", "ayakkabi/sneaker", ProductColor.White, 2),
        new("black sneakers", "ayakkabi/sneaker", ProductColor.Black, 2),
        new("grey runners", "ayakkabi/sneaker", ProductColor.Grey, 1),
        new("brown boots", "ayakkabi/bot", ProductColor.Brown, 3, Seasons: Season.Autumn | Season.Winter),
        new("black boots", "ayakkabi/bot", ProductColor.Black, 3, Seasons: Season.Autumn | Season.Winter),
        new("brown loafers", "ayakkabi/loafer", ProductColor.Brown, 4),
        new("black loafers", "ayakkabi/loafer", ProductColor.Black, 4),
        new("black puffer", "dis-giyim/mont", ProductColor.Black, 2, Seasons: Season.Autumn | Season.Winter),
        new("green parka", "dis-giyim/mont", ProductColor.Green, 2, Seasons: Season.Autumn | Season.Winter),
        new("camel coat", "dis-giyim/kaban", ProductColor.Beige, 4, Seasons: Season.Autumn | Season.Winter),
        new("navy jacket", "dis-giyim/ceket", ProductColor.Navy, 3, Seasons: Season.Spring | Season.Autumn),
        new("navy blazer", "ust-giyim/blazer", ProductColor.Navy, 4),
        new("check blazer", "ust-giyim/blazer", ProductColor.Grey, 4, ProductPattern.Checked),
        new("black bag", "aksesuar/canta", ProductColor.Black, 3),
        new("brown belt", "aksesuar/kemer", ProductColor.Brown, 3),
        new("beige tote", "aksesuar/canta", ProductColor.Beige, 2),
    ];

    /// <summary>Inserts the pieces in one go and returns their IDs by name.</summary>
    public static Task<Dictionary<string, Guid>> InsertWardrobeAsync(this ApiFactory factory, Guid userId, IEnumerable<WardrobeSeed> seeds, CancellationToken cancellationToken) =>
        factory.WithDbAsync(async db =>
        {
            var categories = await db.Categories.ToDictionaryAsync(category => category.Slug, category => category.Id, cancellationToken);
            var items = seeds.Select(seed => (seed.Name, Item: new WardrobeItem
            {
                UserId = userId,
                Name = seed.Name,
                CategoryId = categories[seed.CategorySlug],
                PrimaryColor = seed.Color,
                Pattern = seed.Pattern,
                Fit = seed.Fit,
                Formality = seed.Formality,
                Seasons = seed.Seasons,
                Status = WardrobeItemStatus.Ready,
                ConfirmedAt = DateTimeOffset.UtcNow,
            })).ToList();

            db.WardrobeItems.AddRange(items.Select(item => item.Item));
            await db.SaveChangesAsync(cancellationToken);

            return items.ToDictionary(item => item.Name, item => item.Item.Id);
        });

    public static async Task SetCityAsync(this HttpClient client, string? city, CancellationToken cancellationToken = default)
    {
        using var response = await client.PutAsJsonAsync("/api/v1/me/profile", new { city }, cancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(cancellationToken));
    }

    public static Task<JsonElement> DailyAsync(this HttpClient client, string query = "", CancellationToken cancellationToken = default) =>
        client.GetJsonAsync($"{Base}/daily{query}", cancellationToken);

    public static Task<JsonElement> HowToWearAsync(this HttpClient client, string query, CancellationToken cancellationToken = default) =>
        client.GetJsonAsync($"{Base}/how-to-wear{query}", cancellationToken);

    public static Task<HttpResponseMessage> PostOutfitAsync(this HttpClient client, Guid outfitId, string action, object? body = null, CancellationToken cancellationToken = default) =>
        body is null
            ? client.PostAsync($"{Base}/{outfitId}/{action}", null, cancellationToken)
            : client.PostAsJsonAsync($"{Base}/{outfitId}/{action}", body, cancellationToken);

    public static IEnumerable<JsonElement> Pieces(this JsonElement outfit) => outfit.GetProperty("pieces").EnumerateArray();

    public static JsonElement? Slot(this JsonElement outfit, string slot) =>
        outfit.Pieces().Select(piece => (JsonElement?)piece).FirstOrDefault(piece => piece!.Value.GetProperty("slot").GetString() == slot);

    /// <summary>The key a piece has in the engine: its wardrobe item or product ID.</summary>
    public static string Key(this JsonElement piece) =>
        piece.GetProperty("wardrobeItemId").ValueKind == JsonValueKind.Null
            ? $"p:{piece.GetProperty("productId").GetGuid()}"
            : $"w:{piece.GetProperty("wardrobeItemId").GetGuid()}";

    /// <summary>Slots (top, bottom, shoes, layer) in which two outfits wear different pieces, as the engine counts it.</summary>
    public static int Difference(JsonElement left, JsonElement right) =>
        ComparedSlots.Count(slot => left.Slot(slot)?.Key() != right.Slot(slot)?.Key());

    /// <summary>
    /// Asserts the outfit is wearable by the seeded traits: one top, bottom and shoes; core formality within one step and the layer
    /// within one more; at most one strong pattern.
    /// </summary>
    public static void AssertWearable(JsonElement outfit, IReadOnlyDictionary<Guid, WardrobeSeed> seeds)
    {
        var pieces = outfit.Pieces().ToList();
        foreach (var slot in CoreSlots)
        {
            Assert.Single(pieces, piece => piece.GetProperty("slot").GetString() == slot);
        }

        var owned = pieces.Where(piece => piece.GetProperty("wardrobeItemId").ValueKind != JsonValueKind.Null)
            .Select(piece => (Slot: piece.GetProperty("slot").GetString(), Seed: seeds[piece.GetProperty("wardrobeItemId").GetGuid()]))
            .ToList();
        var core = owned.Where(piece => piece.Slot != "Outerwear").Select(piece => (int)piece.Seed.Formality).ToList();
        Assert.True(core.Max() - core.Min() <= 1, $"formality clash: {string.Join(", ", owned.Select(piece => $"{piece.Seed.Name} {piece.Seed.Formality}"))}");

        if (owned.FirstOrDefault(piece => piece.Slot == "Outerwear") is { Seed: { } layer })
        {
            Assert.InRange(layer.Formality, core.Min() - 1, core.Max() + 1);
        }

        var strong = owned.Count(piece => piece.Seed.Pattern is ProductPattern.Striped or ProductPattern.Checked or ProductPattern.Printed);
        Assert.True(strong <= 1, $"two strong patterns: {string.Join(", ", owned.Select(piece => piece.Seed.Name))}");
    }

    /// <summary>Upserts one product to a fresh brand store with the given traits and price; returns its ID.</summary>
    public static async Task<Guid> SeedProductAsync(
        this HttpClient client, string category, string color, int formality, decimal price = 799m, string audience = "Unisex", string? sellerSlug = null, string? brand = null, string? modelCode = null, decimal? shippingCost = null, decimal? freeShippingThreshold = null, CancellationToken cancellationToken = default)
    {
        brand ??= Unique("Rec");
        var externalId = $"rec-{Guid.NewGuid():N}";
        var item = new
        {
            externalId,
            url = $"https://shop.example.com/p/{externalId}",
            title = $"{brand} {category}",
            price,
            shippingCost,
            freeShippingThreshold,
            stockStatus = "InStock",
            availableSizes = new[] { "M" },
            imageUrls = new[] { $"https://img.example.com/{externalId}.jpg" },
            product = new { brandName = brand, name = $"Parça {category[(category.IndexOf('/') + 1)..]}", modelCode, categorySlug = category, audience, primaryColor = color, formality },
        };

        var body = await client.UpsertOkAsync(sellerSlug ?? $"{Slug(brand)}-store", [item], cancellationToken);

        return body.GetProperty("results")[0].GetProperty("productId").GetGuid();
    }
}
