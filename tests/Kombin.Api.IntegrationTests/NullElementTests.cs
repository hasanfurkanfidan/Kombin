using System.Net;
using System.Net.Http.Json;
using static Kombin.Api.IntegrationTests.CatalogClientExtensions;
using static Kombin.Api.IntegrationTests.ProfileClientExtensions;
using static Kombin.Api.IntegrationTests.TripClientExtensions;

namespace Kombin.Api.IntegrationTests;

/// <summary>Night review item 3: a JSON <c>null</c> inside a list is a mistake of the caller (400), never a crash (500).</summary>
public sealed class NullElementTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task AssertBadRequestAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{(int)response.StatusCode}: {text}");
        response.Dispose();
    }

    [Fact]
    public async Task AnullTripOccasionIsA400ForPlanningAndForSaving()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var request = TripRequest("Istanbul", days: 2, occasions: new object?[] { null });

        await AssertBadRequestAsync(await user.Client.PostAsJsonAsync("/api/v1/me/trips/plan", request, Ct));
        await AssertBadRequestAsync(await user.Client.PostAsJsonAsync("/api/v1/me/trips", new { name = "Gezi", request }, Ct));
    }

    [Fact]
    public async Task ANullSwipeIsA400()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var persona = await user.Client.CreatePersonaAsync("Ofis", cancellationToken: Ct);

        await AssertBadRequestAsync(await user.Client.PostAsJsonAsync($"/api/v1/me/personas/{persona}/swipes", new { swipes = new object?[] { null } }, Ct));
    }

    [Fact]
    public async Task ANullTagIsA400()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var postId = await factory.CreateProcessedPostAsync(user.Client, cancellationToken: Ct);

        await AssertBadRequestAsync(await user.Client.PutAsJsonAsync($"/api/v1/posts/{postId}/publish", new { visibility = "Private", tags = new object?[] { null } }, Ct));
    }

    [Fact]
    public async Task ANullInAnyOtherListIsA400Too()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var itemId = await factory.InsertItemAsync(user.Id, "ust-giyim/tisort", Kombin.Api.Persistence.Entities.Catalog.ProductColor.White, cancellationToken: Ct);
        var brand = Unique("Nul");
        var externalId = $"nul-{Guid.NewGuid():N}";
        object Item(object? images = null, object? sizes = null, object? seasons = null) => new
        {
            externalId,
            url = $"https://shop.example.com/p/{externalId}",
            title = $"{brand} Parça",
            price = 100m,
            stockStatus = "InStock",
            availableSizes = sizes ?? new[] { "M" },
            imageUrls = images ?? new[] { "https://img.example.com/a.jpg" },
            product = new { brandName = brand, name = "Parça", categorySlug = "ust-giyim/tisort", audience = "Unisex", primaryColor = "White", formality = 2, seasons = seasons ?? new[] { "Spring" } },
        };

        // The catalog upsert: the item itself, its images, sizes and seasons.
        using var upsertClient = factory.CreateAnonymousClient();
        var items = new[]
        {
            new object?[] { null },
            [Item(images: new object?[] { null })],
            [Item(sizes: new object?[] { null })],
            [Item(seasons: new object?[] { null })],
        };
        foreach (var batch in items)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/internal/catalog/offers/upsert")
            {
                Content = JsonContent.Create(new { sellerSlug = $"{Slug(brand)}-store", items = batch }),
            };
            request.Headers.Add("X-Internal-Key", ApiFactory.InternalApiKey);
            await AssertBadRequestAsync(await upsertClient.SendAsync(request, Ct));
        }

        // The wardrobe item: its seasons and the fields to clear.
        await AssertBadRequestAsync(await user.Client.PatchAsJsonAsync($"/api/v1/me/wardrobe/items/{itemId}", new { seasons = new object?[] { null } }, Ct));
        await AssertBadRequestAsync(await user.Client.PatchAsJsonAsync($"/api/v1/me/wardrobe/items/{itemId}", new { clear = new object?[] { null } }, Ct));

        // Price alerts: the sizes.
        var product = await factory.SeedProductAsync(upsertClient, 300m, cancellationToken: Ct);
        await AssertBadRequestAsync(await user.Client.PostAsJsonAsync("/api/v1/me/alerts", new { productId = product.ProductId, kind = "PriceDrop", targetPrice = 200m, sizes = new object?[] { null } }, Ct));
    }
}
