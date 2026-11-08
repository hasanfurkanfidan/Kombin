using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Kombin.Api.Features.Catalog.Jobs;
using Kombin.Api.Features.Lens;
using Kombin.Api.Persistence.Entities.Catalog;
using Microsoft.EntityFrameworkCore;
using Pgvector;

namespace Kombin.Api.IntegrationTests;

/// <summary>
/// Visual search end to end: the <c>product-embeddings</c> job over seeded products (images from <see cref="FakeCatalogImageHandler"/>,
/// vectors from <see cref="FakeVisionClient.EmbeddingFor"/>) and <c>POST /lens/search</c> with <see cref="FakeVisionClient.LensPhoto(string, string)"/>
/// photos. The search is global and the database shared, so every test uses its own <c>look-&lt;key&gt;</c>: unrelated vectors are ~0 alike,
/// far below the floor, and other tests' products never show up.
/// </summary>
public sealed class LensTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Look() => $"look-{Guid.NewGuid():N}"[..17];

    private static string ImageUrl(string look, string? extra = null) => $"https://img.lens.test/{Guid.NewGuid():N}/{look}{extra}.jpg";

    /// <summary>Upserts one product (one in-stock brand-store offer) with the given image and returns its product and offer IDs.</summary>
    private static async Task<(Guid ProductId, Guid OfferId)> SeedAsync(
        HttpClient client, string imageUrl, string category = "ust-giyim/tisort", string audience = "Unisex", decimal price = 499m, string stockStatus = "InStock")
    {
        var brand = CatalogClientExtensions.Unique("Lens");
        var item = CatalogClientExtensions.Item(
            $"lens-{Guid.NewGuid():N}", price, brand, "Parça", category: category, imageUrls: [imageUrl], audience: audience, stockStatus: stockStatus, color: "Navy");
        var body = await client.UpsertOkAsync($"{CatalogClientExtensions.Slug(brand)}-store", [item], Ct);
        var result = body.GetProperty("results")[0];

        return (result.GetProperty("productId").GetGuid(), result.GetProperty("offerId").GetGuid());
    }

    private Task<EmbeddingRunSummary> EmbedAsync(params Guid[] productIds) =>
        factory.RunJobAsync<ProductEmbeddingJob, EmbeddingRunSummary>(job => job.EmbedAsync(productIds, int.MaxValue, null, Ct));

    private Task<ProductEmbedding?> RowAsync(Guid productId) =>
        factory.WithDbAsync(db => db.ProductEmbeddings.AsNoTracking().SingleOrDefaultAsync(row => row.ProductId == productId, Ct));

    private static async Task<HttpResponseMessage> SearchRawAsync(HttpClient client, byte[] photo, string query = "", string contentType = "image/jpeg")
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(photo);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        content.Add(file, "image", "photo.jpg");

        return await client.PostAsync($"/api/v1/lens/search{query}", content, Ct);
    }

    private static async Task<JsonElement> SearchAsync(HttpClient client, byte[] photo, string query = "")
    {
        using var response = await SearchRawAsync(client, photo, query);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode}: {body}");

        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static List<Guid> ResultIds(JsonElement region) =>
        [.. region.GetProperty("results").EnumerateArray().Select(result => result.GetProperty("product").GetProperty("id").GetGuid())];

    [Fact]
    public async Task TheJobEmbedsEachProductFromItsPrimaryImageAndARerunDoesNothing()
    {
        using var client = factory.CreateClient();
        var look = Look();
        var first = await SeedAsync(client, ImageUrl(look, "-a"));
        var second = await SeedAsync(client, ImageUrl(look, "-b"));

        var run = await EmbedAsync(first.ProductId, second.ProductId);
        Assert.Equal(new EmbeddingRunSummary(2, 2, 0, 0, 0, 0, false), run);

        var row = await RowAsync(first.ProductId);
        Assert.NotNull(row);
        Assert.Equal(ProductEmbeddingStatus.Ready, row.Status);
        Assert.Equal(FakeVisionClient.Model, row.Model);
        Assert.NotNull(row.SourceImageId);
        var image = await factory.WithDbAsync(db => db.ProductImages.AsNoTracking().SingleAsync(candidate => candidate.Id == row.SourceImageId, Ct));
        Assert.Equal(image.Url, row.SourceImageUrl);
        Assert.Equal(FakeVisionClient.EmbeddingFor(FakeCatalogImageHandler.TextFor(row.SourceImageUrl)), row.Embedding!.ToArray());

        // Idempotent: nothing is due, nothing is fetched again, the rows are untouched.
        var updatedAt = row.UpdatedAt;
        Assert.Equal(new EmbeddingRunSummary(0, 0, 0, 0, 0, 0, false), await EmbedAsync(first.ProductId, second.ProductId));
        Assert.Equal(1, FakeCatalogImageHandler.Requests(row.SourceImageUrl));
        Assert.Equal(updatedAt, (await RowAsync(first.ProductId))!.UpdatedAt);
    }

    [Fact]
    public async Task AnImageThatIsGoneIsMarkedAndNotFetchedAgainOnTheNextRun()
    {
        using var client = factory.CreateClient();
        var url = ImageUrl(Look(), "-missing");
        var product = await SeedAsync(client, url);

        Assert.Equal(new EmbeddingRunSummary(1, 0, 0, 1, 0, 0, false), await EmbedAsync(product.ProductId));
        var row = (await RowAsync(product.ProductId))!;
        Assert.Equal(ProductEmbeddingStatus.Unusable, row.Status);
        Assert.Equal("image_404", row.FailureReason);
        Assert.Null(row.Embedding);

        Assert.Equal(0, (await EmbedAsync(product.ProductId)).Due);
        Assert.Equal(1, FakeCatalogImageHandler.Requests(url));
    }

    [Fact]
    public async Task TransientFailuresBackOffAndARefusedImageIsUnusable()
    {
        using var client = factory.CreateClient();
        var busy = await SeedAsync(client, ImageUrl(Look(), "-busy"));
        var refused = await SeedAsync(client, ImageUrl(Look(), "-refused"));

        var run = await EmbedAsync(busy.ProductId, refused.ProductId);
        Assert.Equal((2, 1, 1), (run.Due, run.Unusable, run.Failed));

        var busyRow = (await RowAsync(busy.ProductId))!;
        Assert.Equal(ProductEmbeddingStatus.Failed, busyRow.Status);
        Assert.Equal(1, busyRow.Attempts);
        Assert.Equal("image_503", busyRow.FailureReason);
        Assert.True(busyRow.NextAttemptAt > DateTimeOffset.UtcNow.AddMinutes(50));
        Assert.Equal(ProductEmbeddingStatus.Unusable, (await RowAsync(refused.ProductId))!.Status);

        // Neither is due again before its time.
        Assert.Equal(0, (await EmbedAsync(busy.ProductId, refused.ProductId)).Due);
    }

    [Fact]
    public async Task ANewVisionModelOrANewImageMakesAProductDueAgainAndASharedImageIsReused()
    {
        using var client = factory.CreateClient();
        var url = ImageUrl(Look());
        var first = await SeedAsync(client, url);
        await EmbedAsync(first.ProductId);

        // A second product with the very same image takes the stored vector without a download.
        var twin = await SeedAsync(client, url);
        Assert.Equal(1, (await EmbedAsync(twin.ProductId)).Reused);
        Assert.Equal(1, FakeCatalogImageHandler.Requests(url));

        await factory.WithDbAsync(async db =>
        {
            var row = await db.ProductEmbeddings.SingleAsync(candidate => candidate.ProductId == first.ProductId, Ct);
            row.Model = "ViT-B-32/an-older-checkpoint";
            return await db.SaveChangesAsync(Ct);
        });

        var rerun = await EmbedAsync(first.ProductId, twin.ProductId);
        Assert.Equal(1, rerun.Due);
        Assert.Equal(FakeVisionClient.Model, (await RowAsync(first.ProductId))!.Model);
    }

    [Fact]
    public async Task APhotoOfAProductFindsThatProductFirstWithItsCheapestOfferBehindALensGoPath()
    {
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        var look = Look();
        var url = ImageUrl(look, "-own");
        var own = await SeedAsync(client, url);
        var lookalike = await SeedAsync(client, ImageUrl(look, "-other"));
        await EmbedAsync(own.ProductId, lookalike.ProductId);

        var body = await SearchAsync(client, FakeVisionClient.LensPhoto(FakeCatalogImageHandler.TextFor(url), "ust-giyim/tisort"));

        Assert.False(body.GetProperty("wholePhoto").GetBoolean());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("audience").ValueKind);
        var region = body.GetProperty("regions")[0];
        Assert.Equal("Top", region.GetProperty("family").GetString());
        Assert.True(region.GetProperty("familyRestricted").GetBoolean());
        Assert.False(region.TryGetProperty("wardrobeMatches", out _));

        Assert.Equal([own.ProductId, lookalike.ProductId], ResultIds(region));
        var top = region.GetProperty("results")[0];
        Assert.Equal(1.0, top.GetProperty("similarity").GetDouble(), 3);
        Assert.Equal(LensRanking.Close, top.GetProperty("level").GetString());
        Assert.True(top.GetProperty("inStock").GetBoolean());
        Assert.Equal(499m, top.GetProperty("product").GetProperty("lowestPrice").GetDecimal());

        var offer = top.GetProperty("offer");
        Assert.Equal(own.OfferId, offer.GetProperty("offerId").GetGuid());
        var goPath = offer.GetProperty("goPath").GetString()!;
        Assert.Equal($"/api/v1/go/{own.OfferId}?src=lens", goPath);
        Assert.DoesNotContain("shop.example.com", body.GetRawText(), StringComparison.Ordinal);

        // Tap-through: the redirect records a click with the lens source.
        using var go = await client.GetAsync(goPath, Ct);
        Assert.Equal(HttpStatusCode.Redirect, go.StatusCode);
        var click = await factory.WithDbAsync(db => db.Clicks.AsNoTracking().SingleAsync(candidate => candidate.OfferId == own.OfferId, Ct));
        Assert.Equal("lens", click.Source);
    }

    [Fact]
    public async Task AConfidentRegionStaysInItsFamilyAndALowConfidenceOneIsWidenedToItsCandidates()
    {
        using var client = factory.CreateClient();
        var look = Look();
        var coat = await SeedAsync(client, ImageUrl(look, "-coat"), category: "dis-giyim/mont");
        var knit = await SeedAsync(client, ImageUrl(look, "-knit"), category: "ust-giyim/triko");
        var sneaker = await SeedAsync(client, ImageUrl(look, "-sneaker"), category: "ayakkabi/sneaker");
        await EmbedAsync(coat.ProductId, knit.ProductId, sneaker.ProductId);

        // Never sneakers for a coat, however alike the vectors.
        var confident = await SearchAsync(client, FakeVisionClient.LensPhoto($"query {look}", "dis-giyim/mont"));
        Assert.Equal([coat.ProductId], ResultIds(confident.GetProperty("regions")[0]));

        var unsure = await SearchAsync(client, FakeVisionClient.LensPhoto($"query {look}", [("dis-giyim/mont", 0.2), ("ust-giyim/triko", 0.18), ("dis-giyim/kaban", 0.1)]));
        var region = unsure.GetProperty("regions")[0];
        Assert.False(region.GetProperty("familyRestricted").GetBoolean());
        Assert.Equal(new[] { coat.ProductId, knit.ProductId }.Order(), ResultIds(region).Order());
    }

    [Fact]
    public async Task NothingBelowTheFloorIsShownAndAPhotoWithoutRegionsIsSearchedWhole()
    {
        using var client = factory.CreateClient();
        var look = Look();
        var product = await SeedAsync(client, ImageUrl(look), category: "aksesuar/canta");
        await EmbedAsync(product.ProductId);

        var unrelated = await SearchAsync(client, FakeVisionClient.LensPhoto($"query {Look()}", "aksesuar/canta"));
        Assert.Empty(unrelated.GetProperty("regions")[0].GetProperty("results").EnumerateArray());

        var whole = await SearchAsync(client, FakeVisionClient.LensPhoto($"query {look}"));
        Assert.True(whole.GetProperty("wholePhoto").GetBoolean());
        var region = Assert.Single(whole.GetProperty("regions").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, region.GetProperty("family").ValueKind);
        Assert.Equal(JsonValueKind.Null, region.GetProperty("category").ValueKind);
        Assert.Equal([product.ProductId], ResultIds(region));
    }

    [Fact]
    public async Task TheProfileAudienceFiltersAndAllDropsTheFilter()
    {
        using var anonymous = factory.CreateClient();
        var look = Look();
        var men = await SeedAsync(anonymous, ImageUrl(look, "-m"), audience: "Men");
        var women = await SeedAsync(anonymous, ImageUrl(look, "-w"), audience: "Women");
        var unisex = await SeedAsync(anonymous, ImageUrl(look, "-u"), audience: "Unisex");
        await EmbedAsync(men.ProductId, women.ProductId, unisex.ProductId);
        var photo = FakeVisionClient.LensPhoto($"query {look}", "ust-giyim/tisort");
        Guid[] all = [men.ProductId, women.ProductId, unisex.ProductId];

        Assert.Equal(all.Order(), ResultIds((await SearchAsync(anonymous, photo)).GetProperty("regions")[0]).Order());

        using var user = await factory.CreateUserAsync(Ct);
        using (var profile = await user.Client.PutAsJsonAsync("/api/v1/me/profile", new { audience = "Women" }, Ct))
        {
            Assert.True(profile.IsSuccessStatusCode, await profile.Content.ReadAsStringAsync(Ct));
        }

        var filtered = await SearchAsync(user.Client, photo);
        Assert.Equal("Women", filtered.GetProperty("audience").GetString());
        Assert.Equal(new[] { women.ProductId, unisex.ProductId }.Order(), ResultIds(filtered.GetProperty("regions")[0]).Order());

        var dropped = await SearchAsync(user.Client, photo, "?audience=all");
        Assert.Equal(JsonValueKind.Null, dropped.GetProperty("audience").ValueKind);
        Assert.Equal(all.Order(), ResultIds(dropped.GetProperty("regions")[0]).Order());

        var men2 = await SearchAsync(anonymous, photo, "?audience=men");
        Assert.Equal(new[] { men.ProductId, unisex.ProductId }.Order(), ResultIds(men2.GetProperty("regions")[0]).Order());

        using var invalid = await SearchRawAsync(anonymous, photo, "?audience=aliens");
        Assert.Equal("Lens.InvalidAudience", (await invalid.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task ASignedInCallerSeesTheirOwnSimilarPiecesAndAnAnonymousOneDoesNot()
    {
        using var anonymous = factory.CreateClient();
        var look = Look();
        var url = ImageUrl(look, "-own");
        var product = await SeedAsync(anonymous, url);
        await EmbedAsync(product.ProductId);

        using var user = await factory.CreateUserAsync(Ct);

        // A piece added from that very product (no photo: its product's vector counts) and a photo piece of the same look, a bit further off.
        using (var added = await user.Client.FromProductAsync(product.ProductId, cancellationToken: Ct))
        {
            Assert.True(added.IsSuccessStatusCode, await added.Content.ReadAsStringAsync(Ct));
        }

        var photoItem = await factory.AddPhotoItemAsync(user.Client, cancellationToken: Ct);
        await factory.WithDbAsync(async db =>
        {
            var item = await db.WardrobeItems.SingleAsync(candidate => candidate.Id == photoItem, Ct);
            item.Embedding = new Vector(FakeVisionClient.EmbeddingFor($"my old one {look} loose"));
            return await db.SaveChangesAsync(Ct);
        });

        var photo = FakeVisionClient.LensPhoto(FakeCatalogImageHandler.TextFor(url), "ust-giyim/tisort");
        var region = (await SearchAsync(user.Client, photo)).GetProperty("regions")[0];
        var matches = region.GetProperty("wardrobeMatches").EnumerateArray().ToList();
        Assert.Equal(2, matches.Count);
        Assert.Equal("exact", matches[0].GetProperty("match").GetString());
        Assert.Equal("similar", matches[1].GetProperty("match").GetString());
        Assert.Equal(photoItem, matches[1].GetProperty("item").GetProperty("id").GetGuid());

        var signedOut = (await SearchAsync(anonymous, photo)).GetProperty("regions")[0];
        Assert.False(signedOut.TryGetProperty("wardrobeMatches", out _));
        Assert.Equal([product.ProductId], ResultIds(signedOut));
    }

    [Fact]
    public async Task ProductsSharingAnImageAreOneResultAndAnInStockOneOutranksItsSoldOutTwin()
    {
        using var client = factory.CreateClient();
        var look = Look();
        var url = ImageUrl(look, "-shared");
        var soldOut = await SeedAsync(client, url, stockStatus: "OutOfStock");
        var inStock = await SeedAsync(client, url, price: 899m);
        await EmbedAsync(soldOut.ProductId);
        await EmbedAsync(inStock.ProductId);

        var region = (await SearchAsync(client, FakeVisionClient.LensPhoto(FakeCatalogImageHandler.TextFor(url), "ust-giyim/tisort"))).GetProperty("regions")[0];
        Assert.Equal([inStock.ProductId], ResultIds(region));
    }

    [Fact]
    public async Task BadUploadsAndAnUnreachableVisionServiceAreProblems()
    {
        using var client = factory.CreateClient();

        using (var missing = await client.PostAsync("/api/v1/lens/search", new MultipartFormDataContent { { new StringContent("x"), "other" } }, Ct))
        {
            Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
            Assert.Equal("Lens.ImageMissing", (await missing.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString());
        }

        using (var text = await SearchRawAsync(client, "hello"u8.ToArray(), contentType: "text/plain"))
        {
            Assert.Equal("Lens.UnsupportedImage", (await text.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString());
        }

        using (var huge = await SearchRawAsync(client, new byte[(8 * 1024 * 1024) + 1]))
        {
            Assert.Equal("Lens.ImageTooLarge", (await huge.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString());
        }

        using (var down = await SearchRawAsync(client, FakeVisionClient.PostPhoto("no-detect")))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, down.StatusCode);
            Assert.Equal("Lens.VisionUnavailable", (await down.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString());
        }
    }

    [Fact]
    public async Task TheRunEndpointIsInternalAndRunsTheJobForTheGivenProducts()
    {
        using var client = factory.CreateClient();
        var product = await SeedAsync(client, ImageUrl(Look()));

        using (var forbidden = await client.PostAsJsonAsync("/api/v1/internal/catalog/embeddings/run", new { productIds = new[] { product.ProductId } }, Ct))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, forbidden.StatusCode);
        }

        using var response = await client.PostInternalAsync("/api/v1/internal/catalog/embeddings/run", new { productIds = new[] { product.ProductId }, limit = 5 }, cancellationToken: Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(1, body.GetProperty("embedded").GetInt32());
        Assert.Equal(0, body.GetProperty("remaining").GetInt32());
    }
}
