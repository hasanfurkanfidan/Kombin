using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kombin.Api.Features.Wardrobe.Jobs;
using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Wardrobe;
using Microsoft.EntityFrameworkCore;

namespace Kombin.Api.IntegrationTests;

/// <summary>
/// The wardrobe against a real database (with pgvector). The vision service is <see cref="FakeVisionClient"/>, storage the in-memory
/// <see cref="FakeObjectStorage"/>, and Hangfire's client only records; the tests run the jobs themselves.
/// </summary>
public sealed class WardrobeTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] SummerAndSpring = ["Summer", "Spring"];

    private static readonly string[] SecondaryColor = ["secondaryColor"];

    private static readonly string[] ImageNames = ["original.jpg", "cutout.png", "thumb.webp"];

    private static string Day(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string[] Strings(JsonElement array) => [.. array.EnumerateArray().Select(element => element.GetString()!)];

    [Fact]
    public async Task APhotoGoesFromUploadUrlThroughProcessAndTheJobToAReadyItemWithAttributesAndImages()
    {
        using var user = await factory.CreateUserAsync(Ct);

        var upload = await user.Client.CreateUploadUrlAsync("image/jpeg", Ct);
        var itemId = upload.GetProperty("itemId").GetGuid();
        Assert.StartsWith("https://storage.test/kombin/wardrobe/", upload.GetProperty("uploadUrl").GetString(), StringComparison.Ordinal);
        Assert.Equal("image/jpeg", upload.GetProperty("headers").GetProperty("Content-Type").GetString());

        var originalKey = await factory.OriginalKeyAsync(itemId, Ct);
        Assert.Equal($"wardrobe/{user.Id}/{itemId}/original.jpg", originalKey);

        // Not uploaded yet: processing refuses, and the unfinished upload does not show in the wardrobe.
        using (var early = await user.Client.ProcessAsync(itemId, Ct))
        {
            Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);
            Assert.Equal("Wardrobe.UploadMissing", (await early.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString());
        }

        Assert.Equal(0, (await user.Client.GetJsonAsync($"{WardrobeClientExtensions.Base}/items", Ct)).GetProperty("totalCount").GetInt32());

        factory.Storage.Upload(originalKey, FakeVisionClient.Photo(), "image/jpeg");

        using (var process = await user.Client.ProcessAsync(itemId, Ct))
        {
            Assert.Equal(HttpStatusCode.Accepted, process.StatusCode);
            Assert.Equal("Processing", (await process.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("status").GetString());
        }

        // Idempotent while processing: a second call enqueues nothing new.
        using (var again = await user.Client.ProcessAsync(itemId, Ct))
        {
            Assert.Equal(HttpStatusCode.Accepted, again.StatusCode);
        }

        var job = Assert.Single(factory.Jobs.JobsFor(itemId));
        Assert.Equal(typeof(WardrobeAnalyzeJob), job.Type);
        Assert.Equal(nameof(WardrobeAnalyzeJob.RunAsync), job.Method.Name);

        var processing = await user.Client.GetItemAsync(itemId, Ct);
        Assert.Equal("Processing", processing.GetProperty("status").GetString());
        Assert.False(processing.GetProperty("needsCategory").GetBoolean());

        Assert.Equal(AnalyzeOutcome.Ready, await factory.AnalyzeAsync(itemId, Ct));

        var item = await user.Client.GetItemAsync(itemId, Ct);
        Assert.Equal("Ready", item.GetProperty("status").GetString());
        Assert.Equal("ust-giyim/tisort", item.GetProperty("category").GetProperty("slug").GetString());
        Assert.False(item.GetProperty("needsCategory").GetBoolean());
        Assert.Equal("Beige", item.GetProperty("primaryColor").GetString());
        Assert.Equal("White", item.GetProperty("secondaryColor").GetString());
        Assert.Equal("Solid", item.GetProperty("pattern").GetString());
        Assert.Equal(2, item.GetProperty("formality").GetInt32());
        Assert.Equal($"{FakeObjectStorage.PublicBaseUrl}/wardrobe/{user.Id}/{itemId}/thumb.webp", item.GetProperty("thumbUrl").GetString());
        Assert.Equal($"{FakeObjectStorage.PublicBaseUrl}/wardrobe/{user.Id}/{itemId}/cutout.png", item.GetProperty("cutoutUrl").GetString());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("originalUrl").ValueKind);
        Assert.Empty(item.GetProperty("warnings").EnumerateArray());
        Assert.Equal(0, item.GetProperty("wearCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("confirmedAt").ValueKind);

        var analysis = item.GetProperty("analysis");
        Assert.Equal(3, analysis.GetProperty("categoryCandidates").GetArrayLength());
        Assert.Equal(0.92, analysis.GetProperty("category").GetProperty("confidence").GetDouble());
        Assert.Equal(0.78, analysis.GetProperty("primaryColor").GetProperty("confidence").GetDouble());
        Assert.Equal(0.95, analysis.GetProperty("pattern").GetProperty("confidence").GetDouble());

        Assert.Equal(FakeVisionClient.Cutout, factory.Storage.Find($"wardrobe/{user.Id}/{itemId}/cutout.png")!.Content);
        Assert.Equal(FakeVisionClient.Thumbnail, factory.Storage.Find($"wardrobe/{user.Id}/{itemId}/thumb.webp")!.Content);

        var row = await factory.ItemRowAsync(itemId, Ct);
        Assert.Equal(512, row.Embedding!.ToArray().Length);
        Assert.Equal(1f, row.Embedding.ToArray()[0]);
        Assert.Equal(FakeVisionClient.Model, row.EmbeddingModel);
        Assert.DoesNotContain("cutout-png", row.AnalysisJson!, StringComparison.Ordinal);

        // A rerun of the job finds nothing to do; processing a ready item is accepted without a new job.
        Assert.Equal(AnalyzeOutcome.Skipped, await factory.AnalyzeAsync(itemId, Ct));
        using (var ready = await user.Client.ProcessAsync(itemId, Ct))
        {
            Assert.Equal(HttpStatusCode.Accepted, ready.StatusCode);
        }

        Assert.Single(factory.Jobs.JobsFor(itemId));

        var list = await user.Client.GetJsonAsync($"{WardrobeClientExtensions.Base}/items", Ct);
        var listed = Assert.Single(list.GetProperty("items").EnumerateArray());
        Assert.Equal(itemId, listed.GetProperty("id").GetGuid());
        Assert.Equal("ust-giyim/tisort", listed.GetProperty("categorySlug").GetString());
    }

    [Fact]
    public async Task AnUnsureCategoryLeavesTheItemReadyWithoutOneAndTheCandidatesToPickFrom()
    {
        using var user = await factory.CreateUserAsync(Ct);

        var itemId = await factory.AddPhotoItemAsync(user.Client, "unsure", Ct);

        var item = await user.Client.GetItemAsync(itemId, Ct);
        Assert.Equal("Ready", item.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("category").ValueKind);
        Assert.True(item.GetProperty("needsCategory").GetBoolean());
        Assert.Equal(
            ["ust-giyim/tisort", "ust-giyim/gomlek", "ust-giyim/triko"],
            item.GetProperty("analysis").GetProperty("categoryCandidates").EnumerateArray().Select(candidate => candidate.GetProperty("slug").GetString()));

        // The one tap: picking a chip confirms the item.
        using var patch = await user.Client.PatchItemAsync(itemId, new { categorySlug = "ust-giyim/gomlek" }, Ct);
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        var patched = await patch.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("ust-giyim/gomlek", patched.GetProperty("category").GetProperty("slug").GetString());
        Assert.False(patched.GetProperty("needsCategory").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, patched.GetProperty("confirmedAt").ValueKind);
    }

    [Fact]
    public async Task APhotoThatIsNotAGarmentIsStillReadyWithAWarning()
    {
        using var user = await factory.CreateUserAsync(Ct);

        var itemId = await factory.AddPhotoItemAsync(user.Client, "not-garment", Ct);

        var item = await user.Client.GetItemAsync(itemId, Ct);
        Assert.Equal("Ready", item.GetProperty("status").GetString());
        Assert.Equal(["not_garment", "person_wearing"], Strings(item.GetProperty("warnings")));
        Assert.False(item.GetProperty("analysis").GetProperty("quality").GetProperty("isGarment").GetBoolean());
    }

    [Fact]
    public async Task AnUnreachableVisionServiceIsRetriedAndTheFourthFailureFailsTheItemUntilItIsProcessedAgain()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var upload = await user.Client.CreateUploadUrlAsync(cancellationToken: Ct);
        var itemId = upload.GetProperty("itemId").GetGuid();
        var originalKey = await factory.OriginalKeyAsync(itemId, Ct);
        factory.Storage.Upload(originalKey, FakeVisionClient.Photo("unreachable"), "image/jpeg");
        using (var process = await user.Client.ProcessAsync(itemId, Ct))
        {
            Assert.Equal(HttpStatusCode.Accepted, process.StatusCode);
        }

        for (var attempt = 1; attempt < WardrobeAnalyzeJob.MaxAttempts; attempt++)
        {
            Assert.Equal(AnalyzeOutcome.Retry, await factory.AnalyzeAsync(itemId, Ct));
            Assert.Equal(attempt, (await factory.ItemRowAsync(itemId, Ct)).AnalysisAttempts);
            Assert.Equal("Processing", (await user.Client.GetItemAsync(itemId, Ct)).GetProperty("status").GetString());
        }

        Assert.Equal(AnalyzeOutcome.Failed, await factory.AnalyzeAsync(itemId, Ct));

        var failed = await user.Client.GetItemAsync(itemId, Ct);
        Assert.Equal("Failed", failed.GetProperty("status").GetString());
        Assert.StartsWith("vision_unreachable", failed.GetProperty("failureReason").GetString(), StringComparison.Ordinal);
        Assert.Equal($"https://storage.test/kombin/{originalKey}?X-Amz-Signature=fake-get", failed.GetProperty("originalUrl").GetString());
        Assert.True(failed.GetProperty("needsCategory").GetBoolean());

        // Processing a failed item starts over: a new job, a clean attempt count. The service is back now.
        factory.Storage.Upload(originalKey, FakeVisionClient.Photo(), "image/jpeg");
        using (var retry = await user.Client.ProcessAsync(itemId, Ct))
        {
            Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
        }

        Assert.Equal(2, factory.Jobs.JobsFor(itemId).Count);
        Assert.Equal(0, (await factory.ItemRowAsync(itemId, Ct)).AnalysisAttempts);
        Assert.Equal(AnalyzeOutcome.Ready, await factory.AnalyzeAsync(itemId, Ct));
    }

    [Fact]
    public async Task ARefusedImageFailsAtOnceAndCanBeFilledInByHand()
    {
        using var user = await factory.CreateUserAsync(Ct);

        var itemId = await factory.AddPhotoItemAsync(user.Client, "refused", Ct);

        var failed = await user.Client.GetItemAsync(itemId, Ct);
        Assert.Equal("Failed", failed.GetProperty("status").GetString());
        Assert.Equal("vision_400: unsupported_image", failed.GetProperty("failureReason").GetString());
        Assert.Equal(0, (await factory.ItemRowAsync(itemId, Ct)).AnalysisAttempts);

        using var patch = await user.Client.PatchItemAsync(itemId, new { categorySlug = "alt-giyim/jean", primaryColor = "Navy" }, Ct);
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        var filled = await patch.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("Ready", filled.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, filled.GetProperty("failureReason").ValueKind);
        Assert.Equal("Navy", filled.GetProperty("primaryColor").GetString());
    }

    [Fact]
    public async Task TheFirstPatchConfirmsTheItemAndLaterOnesEditWithoutMovingTheConfirmation()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var itemId = await factory.AddPhotoItemAsync(user.Client, cancellationToken: Ct);

        using var confirm = await user.Client.PatchItemAsync(itemId, new { }, Ct);
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
        var confirmedAt = (await confirm.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("confirmedAt").GetDateTimeOffset();

        var productId = await user.Client.SeedCatalogProductAsync("ust-giyim/tisort", "Beige", 2, cancellationToken: Ct);
        var brandId = await factory.WithDbAsync(db => db.Products.Where(product => product.Id == productId).Select(product => product.BrandId).SingleAsync(Ct));
        using var edit = await user.Client.PatchItemAsync(
            itemId,
            new
            {
                name = "  Bej basic tişört ",
                primaryColor = "white",
                pattern = "Striped",
                fit = "Oversized",
                formality = 3,
                seasons = SummerAndSpring,
                brandId,
                size = " M ",
                purchasePrice = 349.90m,
                purchasedAt = "2026-10-01",
                clear = SecondaryColor,
            },
            Ct);
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);

        var item = await edit.Content.ReadFromJsonAsync<JsonElement>(Ct);
        // PostgreSQL keeps microseconds; the first answer still had the clock's 100 ns ticks.
        Assert.Equal(confirmedAt.UtcTicks / 10, item.GetProperty("confirmedAt").GetDateTimeOffset().UtcTicks / 10);
        Assert.Equal("Bej basic tişört", item.GetProperty("name").GetString());
        Assert.Equal("White", item.GetProperty("primaryColor").GetString());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("secondaryColor").ValueKind);
        Assert.Equal("Striped", item.GetProperty("pattern").GetString());
        Assert.Equal("Oversized", item.GetProperty("fit").GetString());
        Assert.Equal(3, item.GetProperty("formality").GetInt32());
        Assert.Equal(["Spring", "Summer"], Strings(item.GetProperty("seasons")));
        Assert.Equal(brandId, item.GetProperty("brand").GetProperty("id").GetGuid());
        Assert.Equal("M", item.GetProperty("size").GetString());
        Assert.Equal(349.90m, item.GetProperty("purchasePrice").GetDecimal());
        Assert.Equal("2026-10-01", item.GetProperty("purchasedAt").GetString());

        using var unknown = await user.Client.PatchItemAsync(itemId, new { categorySlug = "no-such/category" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Equal("Wardrobe.UnknownCategory", (await unknown.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString());

        using var invalid = await user.Client.PatchItemAsync(itemId, new { primaryColor = "Turquoise", formality = 9 }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var errors = (await invalid.ReadProblemAsync(Ct)).GetProperty("errors");
        Assert.True(errors.TryGetProperty("primaryColor", out _));
        Assert.True(errors.TryGetProperty("formality", out _));
    }

    [Fact]
    public async Task AProcessingItemCannotBeEdited()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var itemId = (await user.Client.CreateUploadUrlAsync(cancellationToken: Ct)).GetProperty("itemId").GetGuid();

        using var patch = await user.Client.PatchItemAsync(itemId, new { primaryColor = "Black" }, Ct);

        Assert.Equal(HttpStatusCode.Conflict, patch.StatusCode);
        Assert.Equal("Wardrobe.ItemProcessing", (await patch.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task AnUploadMustBeAnAcceptedImageOfAtMostEightMegabytes()
    {
        using var user = await factory.CreateUserAsync(Ct);

        using var gif = await user.Client.CreateUploadUrlRawAsync("image/gif", cancellationToken: Ct);
        Assert.Equal(HttpStatusCode.BadRequest, gif.StatusCode);
        Assert.True((await gif.ReadProblemAsync(Ct)).GetProperty("errors").TryGetProperty("contentType", out _));

        using var huge = await user.Client.CreateUploadUrlRawAsync("image/png", (8 * 1024 * 1024) + 1, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, huge.StatusCode);

        // The size announced is not the size uploaded: process checks the stored object.
        var upload = await user.Client.CreateUploadUrlAsync("image/png", Ct);
        var itemId = upload.GetProperty("itemId").GetGuid();
        Assert.EndsWith("/original.png", await factory.OriginalKeyAsync(itemId, Ct), StringComparison.Ordinal);
        factory.Storage.Upload(await factory.OriginalKeyAsync(itemId, Ct), new byte[(8 * 1024 * 1024) + 1], "image/png");

        using var process = await user.Client.ProcessAsync(itemId, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, process.StatusCode);
        Assert.Equal("Wardrobe.ImageTooLarge", (await process.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task AddingAProductCopiesItsImageAndAttributesIntoAReadyConfirmedItemOnce()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var imageUrl = $"https://img.example.com/{Guid.NewGuid():N}.jpg";
        var productId = await user.Client.SeedCatalogProductAsync("ust-giyim/gomlek", "Navy", 4, imageUrl, Ct);

        using var created = await user.Client.FromProductAsync(productId, "L", Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var item = await created.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var itemId = item.GetProperty("id").GetGuid();
        Assert.Equal("Ready", item.GetProperty("status").GetString());
        Assert.True(item.GetProperty("fromCatalog").GetBoolean());
        Assert.Equal(productId, item.GetProperty("productId").GetGuid());
        Assert.Equal("ust-giyim/gomlek", item.GetProperty("category").GetProperty("slug").GetString());
        Assert.Equal("Navy", item.GetProperty("primaryColor").GetString());
        Assert.Equal(4, item.GetProperty("formality").GetInt32());
        Assert.Equal("L", item.GetProperty("size").GetString());
        Assert.NotEqual(JsonValueKind.Null, item.GetProperty("brand").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, item.GetProperty("confirmedAt").ValueKind);
        Assert.Equal($"{FakeObjectStorage.PublicBaseUrl}/wardrobe/{user.Id}/{itemId}/thumb.jpg", item.GetProperty("thumbUrl").GetString());
        Assert.Equal($"{FakeObjectStorage.PublicBaseUrl}/wardrobe/{user.Id}/{itemId}/cutout.jpg", item.GetProperty("cutoutUrl").GetString());

        // The catalog image as it was, under both names: no background removal for catalog shots.
        Assert.Equal(FakeCatalogImageHandler.BytesFor(imageUrl), factory.Storage.Find($"wardrobe/{user.Id}/{itemId}/cutout.jpg")!.Content);
        Assert.Equal(FakeCatalogImageHandler.BytesFor(imageUrl), factory.Storage.Find($"wardrobe/{user.Id}/{itemId}/thumb.jpg")!.Content);

        // A double tap does not add it twice.
        using var again = await user.Client.FromProductAsync(productId, "L", Ct);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(itemId, (await again.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid());

        using var unknown = await user.Client.FromProductAsync(Guid.NewGuid(), cancellationToken: Ct);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("Catalog.ProductNotFound", (await unknown.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task AProductWhoseImageCannotBeCopiedStillGetsAddedAndShowsTheCatalogImage()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var imageUrl = $"https://img.example.com/missing-{Guid.NewGuid():N}.jpg";
        var productId = await user.Client.SeedCatalogProductAsync("ayakkabi/sneaker", "White", 2, imageUrl, Ct);

        using var created = await user.Client.FromProductAsync(productId, cancellationToken: Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var item = await created.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(imageUrl, item.GetProperty("thumbUrl").GetString());
        Assert.Equal(imageUrl, item.GetProperty("cutoutUrl").GetString());
        Assert.Null((await factory.ItemRowAsync(item.GetProperty("id").GetGuid(), Ct)).ImageThumbKey);
    }

    [Fact]
    public async Task WearingIsOneRowPerDayAndUndoingItUpdatesTheCountAndTheLastDay()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var itemId = await factory.InsertItemAsync(user.Id, "ust-giyim/tisort", ProductColor.White, cancellationToken: Ct);
        var today = Kombin.Api.Features.Wardrobe.WearRules.Today(DateTimeOffset.UtcNow);

        async Task<JsonElement> WearOkAsync(DateOnly? day)
        {
            using var response = await user.Client.WearAsync(itemId, day, Ct);
            Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));

            return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        }

        var first = await WearOkAsync(null);
        Assert.Equal(Day(today), first.GetProperty("wornOn").GetString());
        Assert.Equal(1, first.GetProperty("wearCount").GetInt32());

        Assert.Equal(1, (await WearOkAsync(today)).GetProperty("wearCount").GetInt32());

        var earlier = await WearOkAsync(today.AddDays(-3));
        Assert.Equal(2, earlier.GetProperty("wearCount").GetInt32());
        Assert.Equal(Day(today), earlier.GetProperty("lastWornAt").GetString());

        using (var future = await user.Client.WearAsync(itemId, today.AddDays(1), Ct))
        {
            Assert.Equal(HttpStatusCode.BadRequest, future.StatusCode);
            Assert.True((await future.ReadProblemAsync(Ct)).GetProperty("errors").TryGetProperty("wornOn", out _));
        }

        using (var undo = await user.Client.DeleteAsync($"{WardrobeClientExtensions.Base}/items/{itemId}/wear/{today:yyyy-MM-dd}", Ct))
        {
            Assert.Equal(HttpStatusCode.OK, undo.StatusCode);
            var body = await undo.Content.ReadFromJsonAsync<JsonElement>(Ct);
            Assert.Equal(1, body.GetProperty("wearCount").GetInt32());
            Assert.Equal(Day(today.AddDays(-3)), body.GetProperty("lastWornAt").GetString());
        }

        // Undoing a day that is not there changes nothing.
        using (var again = await user.Client.DeleteAsync($"{WardrobeClientExtensions.Base}/items/{itemId}/wear/{today:yyyy-MM-dd}", Ct))
        {
            Assert.Equal(1, (await again.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("wearCount").GetInt32());
        }

        var item = await user.Client.GetItemAsync(itemId, Ct);
        Assert.Equal(1, item.GetProperty("wearCount").GetInt32());
        Assert.Equal(Day(today.AddDays(-3)), item.GetProperty("lastWornAt").GetString());
    }

    [Fact]
    public async Task StatsCountTheWardrobeByCategoryAndColorAndRankWhatIsWornMostAndLeast()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var favorite = await factory.InsertItemAsync(user.Id, "ust-giyim/tisort", ProductColor.White, cancellationToken: Ct);
        var jeans = await factory.InsertItemAsync(user.Id, "alt-giyim/jean", ProductColor.Navy, cancellationToken: Ct);
        var forgotten = await factory.InsertItemAsync(user.Id, "ust-giyim/tisort", ProductColor.Black, cancellationToken: Ct);
        var archived = await factory.InsertItemAsync(user.Id, "ayakkabi/sneaker", ProductColor.White, cancellationToken: Ct);

        var today = Kombin.Api.Features.Wardrobe.WearRules.Today(DateTimeOffset.UtcNow);
        foreach (var day in new[] { today, today.AddDays(-1), today.AddDays(-2) })
        {
            using var wear = await user.Client.WearAsync(favorite, day, Ct);
            Assert.Equal(HttpStatusCode.OK, wear.StatusCode);
        }

        using (var wear = await user.Client.WearAsync(jeans, today.AddDays(-200), Ct))
        {
            Assert.Equal(HttpStatusCode.OK, wear.StatusCode);
        }

        // Added long ago and never worn.
        var longAgo = DateTimeOffset.UtcNow.AddDays(-300);
        await factory.WithDbAsync(db => db.WardrobeItems.Where(item => item.Id == forgotten).ExecuteUpdateAsync(set => set.SetProperty(item => item.CreatedAt, longAgo), Ct));

        using (var delete = await user.Client.DeleteAsync($"{WardrobeClientExtensions.Base}/items/{archived}", Ct))
        {
            Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        }

        var stats = await user.Client.GetJsonAsync($"{WardrobeClientExtensions.Base}/stats", Ct);

        Assert.Equal(3, stats.GetProperty("itemCount").GetInt32());
        Assert.Equal(
            [("ust-giyim/tisort", 2), ("alt-giyim/jean", 1)],
            stats.GetProperty("byCategory").EnumerateArray().Select(row => (row.GetProperty("slug").GetString(), row.GetProperty("count").GetInt32())));
        Assert.Equal(3, stats.GetProperty("byColor").GetArrayLength());
        Assert.Equal(2, stats.GetProperty("unwornFor180Days").GetInt32());
        Assert.Equal(JsonValueKind.Null, stats.GetProperty("possibleOutfits").ValueKind);

        var mostWorn = stats.GetProperty("mostWorn").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).ToList();
        Assert.Equal([favorite, jeans], mostWorn);
        Assert.Equal(3, stats.GetProperty("mostWorn")[0].GetProperty("wearCount").GetInt32());

        var leastWorn = stats.GetProperty("leastWorn").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).ToList();
        Assert.Equal([forgotten, jeans, favorite], leastWorn);
    }

    [Fact]
    public async Task GapsNameTheMissingPieceAndSuggestCatalogProductsThatFillIt()
    {
        using var user = await factory.CreateUserAsync(Ct);

        // Casual is covered; at smart casual there are bottoms and shoes but no top.
        await factory.InsertItemAsync(user.Id, "ust-giyim/tisort", ProductColor.White, 2, Ct);
        await factory.InsertItemAsync(user.Id, "alt-giyim/jean", ProductColor.Navy, 2, Ct);
        await factory.InsertItemAsync(user.Id, "ayakkabi/sneaker", ProductColor.Black, 2, Ct);
        await factory.InsertItemAsync(user.Id, "dis-giyim/mont", ProductColor.Black, 2, Ct);
        await factory.InsertItemAsync(user.Id, "alt-giyim/pantolon", ProductColor.Beige, 3, Ct);
        await factory.InsertItemAsync(user.Id, "ayakkabi/loafer", ProductColor.Brown, 3, Ct);

        var fits = await user.Client.SeedCatalogProductAsync("ust-giyim/gomlek", "White", 3, cancellationToken: Ct);
        var wrongColor = await user.Client.SeedCatalogProductAsync("ust-giyim/gomlek", "Red", 3, cancellationToken: Ct);

        var body = await user.Client.GetJsonAsync($"{WardrobeClientExtensions.Base}/gaps", Ct);

        Assert.Equal(JsonValueKind.Null, body.GetProperty("personaId").ValueKind);
        var gap = Assert.Single(body.GetProperty("gaps").EnumerateArray());
        Assert.Equal("missing_smart_top", gap.GetProperty("kind").GetString());
        Assert.Equal("Dolabında hiç smart casual üst yok", gap.GetProperty("message").GetString());
        Assert.Equal("ust-giyim", gap.GetProperty("categorySlug").GetString());
        Assert.Equal("White", gap.GetProperty("color").GetString());
        Assert.Equal(3, gap.GetProperty("formality").GetInt32());
        Assert.Equal(2 * 2, gap.GetProperty("unlocksEstimate").GetInt32());

        var suggested = body.GetProperty("suggestedProducts").EnumerateArray().Select(product => product.GetProperty("id").GetGuid()).ToList();
        Assert.Contains(fits, suggested);
        Assert.DoesNotContain(wrongColor, suggested);
        Assert.InRange(suggested.Count, 1, 3);

        // Once it is in the wardrobe it is no longer suggested, and the gap is closed.
        using (var added = await user.Client.FromProductAsync(fits, cancellationToken: Ct))
        {
            Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        }

        Assert.Empty((await user.Client.GetJsonAsync($"{WardrobeClientExtensions.Base}/gaps", Ct)).GetProperty("gaps").EnumerateArray());
    }

    [Fact]
    public async Task WithAPersonaGapsFollowItsFormalityAndSuggestionsMustMatchItWell()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var personaId = await user.Client.CreatePersonaAsync("Ofis", new { formalityMin = 4, formalityMax = 5 }, Ct);
        await factory.InsertItemAsync(user.Id, "ust-giyim/tisort", ProductColor.White, 2, Ct);

        var body = await user.Client.GetJsonAsync($"{WardrobeClientExtensions.Base}/gaps", Ct);

        Assert.Equal(personaId, body.GetProperty("personaId").GetGuid());
        var kinds = body.GetProperty("gaps").EnumerateArray().Select(gap => gap.GetProperty("kind").GetString()).ToList();
        Assert.Equal(["missing_formal_top", "missing_formal_bottom", "missing_formal_shoes", "missing_outerwear"], kinds);

        // A persona that learned nothing yet is damped to at most 60, so only a perfect match could be suggested.
        Assert.All(body.GetProperty("suggestedProducts").EnumerateArray(), product => Assert.True(product.GetProperty("matchScore").GetInt32() >= 60));
    }

    [Fact]
    public async Task ListsFilterByCategoryColorAndArchiveAndNewestComeFirst()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var shirt = await factory.InsertItemAsync(user.Id, "ust-giyim/gomlek", ProductColor.White, cancellationToken: Ct);
        var tee = await factory.InsertItemAsync(user.Id, "ust-giyim/tisort", ProductColor.Black, cancellationToken: Ct);
        var jeans = await factory.InsertItemAsync(user.Id, "alt-giyim/jean", ProductColor.Navy, cancellationToken: Ct);
        using (var delete = await user.Client.DeleteAsync($"{WardrobeClientExtensions.Base}/items/{jeans}", Ct))
        {
            Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        }

        async Task<List<Guid>> IdsAsync(string query) =>
            [.. (await user.Client.GetJsonAsync($"{WardrobeClientExtensions.Base}/items{query}", Ct)).GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid())];

        Assert.Equal([tee, shirt], await IdsAsync(string.Empty));
        Assert.Equal([tee, shirt], await IdsAsync("?category=ust-giyim"));
        Assert.Equal([shirt], await IdsAsync("?category=ust-giyim/gomlek"));
        Assert.Equal([tee], await IdsAsync("?color=black"));
        Assert.Equal([jeans], await IdsAsync("?archived=true"));

        using var invalid = await user.Client.GetAsync($"{WardrobeClientExtensions.Base}/items?color=Turquoise", Ct);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        // An archived item is still readable, but no longer wearable or editable.
        Assert.True((await user.Client.GetItemAsync(jeans, Ct)).GetProperty("isArchived").GetBoolean());
        using var wear = await user.Client.WearAsync(jeans, cancellationToken: Ct);
        Assert.Equal(HttpStatusCode.Conflict, wear.StatusCode);
        Assert.Equal("Wardrobe.ItemArchived", (await wear.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task CleanupDeletesTheImagesOfItemsArchivedMoreThanThirtyDaysAgoAndAbandonedUploads()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var old = await factory.AddPhotoItemAsync(user.Client, cancellationToken: Ct);
        var recent = await factory.AddPhotoItemAsync(user.Client, cancellationToken: Ct);
        var oldKeys = ImageNames.Select(name => $"wardrobe/{user.Id}/{old}/{name}").ToList();

        foreach (var id in new[] { old, recent })
        {
            using var delete = await user.Client.DeleteAsync($"{WardrobeClientExtensions.Base}/items/{id}", Ct);
            Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        }

        var archivedLongAgo = DateTimeOffset.UtcNow.AddDays(-31);
        await factory.WithDbAsync(db => db.WardrobeItems.Where(item => item.Id == old).ExecuteUpdateAsync(set => set.SetProperty(item => item.ArchivedAt, archivedLongAgo), Ct));

        // An upload started two days ago and never processed.
        var abandoned = (await user.Client.CreateUploadUrlAsync(cancellationToken: Ct)).GetProperty("itemId").GetGuid();
        var abandonedKey = await factory.OriginalKeyAsync(abandoned, Ct);
        factory.Storage.Upload(abandonedKey, FakeVisionClient.Photo(), "image/jpeg");
        var twoDaysAgo = DateTimeOffset.UtcNow.AddDays(-2);
        await factory.WithDbAsync(db => db.WardrobeItems.Where(item => item.Id == abandoned).ExecuteUpdateAsync(set => set.SetProperty(item => item.CreatedAt, twoDaysAgo), Ct));

        var summary = await factory.CleanupWardrobeAsync(user.Id, Ct);

        Assert.Equal(new CleanupSummary(1, 1), summary);
        Assert.All(oldKeys, key => Assert.Null(factory.Storage.Find(key)));
        Assert.All(oldKeys, key => Assert.Contains(key, factory.Storage.Deleted));
        Assert.Null(factory.Storage.Find(abandonedKey));
        Assert.NotNull(factory.Storage.Find($"wardrobe/{user.Id}/{recent}/thumb.webp"));

        var cleaned = await factory.ItemRowAsync(old, Ct);
        Assert.Null(cleaned.ImageOriginalKey);
        Assert.Null(cleaned.ImageThumbKey);
        Assert.False(await factory.WithDbAsync(db => db.WardrobeItems.AnyAsync(item => item.Id == abandoned, Ct)));

        // Idempotent.
        Assert.Equal(new CleanupSummary(0, 0), await factory.CleanupWardrobeAsync(user.Id, Ct));
    }

    [Fact]
    public async Task AnotherUsersItemIsNotFoundEverywhere()
    {
        using var owner = await factory.CreateUserAsync(Ct);
        using var stranger = await factory.CreateUserAsync(Ct);
        var itemId = await factory.InsertItemAsync(owner.Id, "ust-giyim/tisort", ProductColor.White, cancellationToken: Ct);
        var url = $"{WardrobeClientExtensions.Base}/items/{itemId}";

        var responses = new List<HttpResponseMessage>
        {
            await stranger.Client.GetAsync(url, Ct),
            await stranger.Client.PatchItemAsync(itemId, new { }, Ct),
            await stranger.Client.ProcessAsync(itemId, Ct),
            await stranger.Client.WearAsync(itemId, cancellationToken: Ct),
            await stranger.Client.DeleteAsync($"{url}/wear/2026-10-01", Ct),
            await stranger.Client.DeleteAsync(url, Ct),
        };

        foreach (var response in responses)
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("Wardrobe.ItemNotFound", (await response.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString());
            response.Dispose();
        }

        Assert.False((await owner.Client.GetItemAsync(itemId, Ct)).GetProperty("isArchived").GetBoolean());
        Assert.Equal(0, (await stranger.Client.GetJsonAsync($"{WardrobeClientExtensions.Base}/items", Ct)).GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task TheWardrobeNeedsASignedInUser()
    {
        using var client = factory.CreateAnonymousClient();

        using var response = await client.GetAsync($"{WardrobeClientExtensions.Base}/items", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
