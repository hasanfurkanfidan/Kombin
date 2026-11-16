using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using static Kombin.Api.IntegrationTests.WardrobeClientExtensions;

namespace Kombin.Api.IntegrationTests;

/// <summary>Night review item 5: "Dolabıma ekle" survives a failing store and a double tap.</summary>
public sealed class FromProductRobustnessTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("cutout")]
    [InlineData("thumb")]
    public async Task AFailingStoreStillCreatesThePieceWithoutImagesAndLeavesNoObjectBehind(string failing)
    {
        using var user = await factory.CreateUserAsync(Ct);
        var productId = await user.Client.SeedCatalogProductAsync("ust-giyim/tisort", "White", 2, imageUrl: "https://img.example.com/store-down.jpg", cancellationToken: Ct);
        var name = $"b6-{Guid.NewGuid():N}";
        factory.Storage.FailPuts(name, key => key.Contains($"/{user.Id}/", StringComparison.Ordinal) && key.Contains($"/{failing}.", StringComparison.Ordinal));

        try
        {
            using var response = await user.Client.FromProductAsync(productId, cancellationToken: Ct);
            var text = await response.Content.ReadAsStringAsync(Ct);

            Assert.True(response.StatusCode == HttpStatusCode.Created, $"{(int)response.StatusCode}: {text}");
            using var body = JsonDocument.Parse(text);

            // No copy of ours: the item shows the catalog's own picture, as when a download fails.
            var item = body.RootElement;
            Assert.Equal("https://img.example.com/store-down.jpg", item.GetProperty("thumbUrl").GetString());
            var row = await factory.ItemRowAsync(item.GetProperty("id").GetGuid(), Ct);
            Assert.Null(row.ImageCutoutKey);
            Assert.Null(row.ImageThumbKey);
            Assert.True(row.FromCatalog);
            Assert.Equal(Kombin.Api.Persistence.Entities.Wardrobe.WardrobeItemStatus.Ready, row.Status);

            // Whatever was written before the failure is taken back: no orphan.
            Assert.Empty(factory.Storage.KeysOfUser(user.Id));
        }
        finally
        {
            factory.Storage.StopFailingPuts(name);
        }
    }

    [Fact]
    public async Task ADoubleTapOnASlowImageCreatesOnePiece()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var productId = await user.Client.SeedCatalogProductAsync("ust-giyim/tisort", "White", 2, imageUrl: $"https://img.example.com/slow-{Guid.NewGuid():N}.jpg", cancellationToken: Ct);

        var taps = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => user.Client.FromProductAsync(productId, cancellationToken: Ct)));

        // One tap created it (201), the others got that piece back (200).
        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.Created], taps.Select(tap => tap.StatusCode).Order());
        var ids = new List<Guid>();
        foreach (var tap in taps)
        {
            ids.Add((await tap.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid());
            tap.Dispose();
        }

        Assert.Single(ids.Distinct());
        Assert.Equal(1, await factory.WithDbAsync(db => db.WardrobeItems.CountAsync(item => item.UserId == user.Id && item.ProductId == productId, Ct)));

        // The losers' copies of the image are not left in the store either: exactly the winner's two objects.
        Assert.Equal(2, factory.Storage.KeysOfUser(user.Id).Count);
    }

    [Fact]
    public async Task APieceThatWasRemovedCanBeAddedAgain()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var productId = await user.Client.SeedCatalogProductAsync("ust-giyim/tisort", "White", 2, cancellationToken: Ct);

        using var first = await user.Client.FromProductAsync(productId, cancellationToken: Ct);
        var firstId = (await first.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        using (var removed = await user.Client.DeleteAsync($"/api/v1/me/wardrobe/items/{firstId}", Ct))
        {
            Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        }

        using var again = await user.Client.FromProductAsync(productId, cancellationToken: Ct);
        Assert.Equal(HttpStatusCode.Created, again.StatusCode);
        Assert.NotEqual(firstId, (await again.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid());
    }
}
