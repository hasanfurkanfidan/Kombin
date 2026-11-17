using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kombin.Api.Common.Storage;
using Kombin.Api.Features.TryOn.Jobs;
using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Recommendation;
using Kombin.Api.Persistence.Entities.TryOn;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Kombin.Api.IntegrationTests;

/// <summary>
/// "Manken üstünde gör" end to end: the readiness rule through the API, preparing (the job, its retries, its limits), drawing with the cache and its
/// invalidations, the render limits, the renderer's refusals, reports and the switch. Every test uses its own marker in the photos' text, so the
/// fake vision service's counters are the test's own.
/// </summary>
[Collection(TryOnJobs.Name)]
public sealed class TryOnTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string NewMarker() => $"m{Guid.NewGuid():N}"[..13];

    private async Task<(TestUser User, Guid Outfit, Guid Top, Guid Trousers, Guid Shoes)> ReadyOutfitAsync(string marker, string topFlavor = "", string? topSize = null, Guid? brandId = null)
    {
        var user = await factory.CreateUserAsync(Ct);
        var (outfit, top, trousers, shoes) = await factory.SeedWardrobeOutfitAsync(user.Id, marker, topFlavor, topSize, brandId);
        await factory.RunTryOnAsync(TestClock.UtcNow, items: [top, trousers, shoes]);

        return (user, outfit, top, trousers, shoes);
    }

    private Task<TryOnGarment?> RowOfItemAsync(Guid itemId) =>
        factory.WithDbAsync(db => db.TryOnGarments.AsNoTracking().SingleOrDefaultAsync(row => row.WardrobeItemId == itemId, Ct));

    private Task<TryOnGarment?> RowOfProductAsync(Guid productId) =>
        factory.WithDbAsync(db => db.TryOnGarments.AsNoTracking().SingleOrDefaultAsync(row => row.ProductId == productId, Ct));

    private Task<List<TryOnRender>> RendersOfAsync(Guid userId) =>
        factory.WithDbAsync(db => db.TryOnRenders.AsNoTracking().Where(render => render.UserId == userId).ToListAsync(Ct));

    [Fact]
    public async Task AnOutfitIsPreparedOnRequestThenDrawnOnceAndServedFromTheCache()
    {
        var marker = NewMarker();
        using var user = await factory.CreateUserAsync(Ct);
        var (outfit, top, trousers, shoes) = await factory.SeedWardrobeOutfitAsync(user.Id, marker);

        var first = await user.Client.GetTryOnOkAsync($"outfitId={outfit}", Ct);
        Assert.Equal("Preparing", first.Status());
        Assert.Empty(first.GetProperty("garments").EnumerateArray());

        // The request created the rows as asked for and enqueued one targeted prepare with the three pieces.
        foreach (var id in new[] { top, trousers, shoes })
        {
            var asked = await RowOfItemAsync(id);
            Assert.Equal(TryOnGarmentStatus.Pending, asked!.Status);
            Assert.NotNull(asked.RequestedAt);
        }

        var enqueued = factory.Jobs.Jobs.Where(job => job.Method.Name == nameof(TryOnPrepareJob.PrepareRequestedAsync) && ((Guid[])job.Args[0]!).Contains(top)).ToList();
        Assert.Single(enqueued);
        Assert.Equal(new[] { top, trousers, shoes }.Order(), ((Guid[])enqueued[0].Args[0]!).Order());

        // Asking again within a minute enqueues nothing more (the app polls).
        await user.Client.GetTryOnOkAsync($"outfitId={outfit}", Ct);
        Assert.Single(factory.Jobs.Jobs, job => job.Method.Name == nameof(TryOnPrepareJob.PrepareRequestedAsync) && ((Guid[])job.Args[0]!).Contains(top));

        // The minute run takes the asked-for rows first.
        var summary = await factory.RunTryOnAsync(TestClock.UtcNow, items: [top, trousers, shoes]);
        Assert.Equal(3, summary.WardrobePrepared);
        var row = await RowOfItemAsync(top);
        Assert.Equal((TryOnGarmentStatus.Prepared, "top", true, true, FakeVisionClient.PrepareVersion), (row!.Status, row.Family, row.Reliable, row.MayBeOutermost, row.PrepareVersion));
        Assert.Null(row.RequestedAt);
        Assert.False(StorageKeys.IsPublic(row.CutoutKey!));
        Assert.EndsWith("/prepared.png", row.CutoutKey, StringComparison.Ordinal);

        var ready = await user.Client.GetTryOnOkAsync($"outfitId={outfit}", Ct);
        Assert.Equal("Ready", ready.Status());
        Assert.Null(ready.Reason());
        Assert.False(string.IsNullOrEmpty(ready.GetProperty("imageUrl").GetString()));
        Assert.Equal([top, trousers, shoes], ready.GetProperty("garments").EnumerateArray().Select(garment => garment.GetProperty("id").GetGuid()));
        Assert.Contains("chest", ready.GetProperty("defaultedMeasurements").EnumerateArray().Select(name => name.GetString()));

        // The drawing is private, stored once, and the second request is a cache hit (no new render).
        var render = Assert.Single(await RendersOfAsync(user.Id));
        Assert.EndsWith("/render.webp", render.ImageKey, StringComparison.Ordinal);
        Assert.False(StorageKeys.IsPublic(render.ImageKey!));
        Assert.Equal(FakeVisionClient.RenderImage, factory.Storage.Find(render.ImageKey!)!.Content);
        await user.Client.GetTryOnOkAsync($"outfitId={outfit}", Ct);
        Assert.Equal(1, FakeVisionClient.RenderCalls(marker));
        Assert.Single(await RendersOfAsync(user.Id));
    }

    [Fact]
    public async Task TheRenderSpecCarriesTheBodySizesAndTheSizeHintAndTheFitLevelComesBack()
    {
        var marker = NewMarker();
        var brand = await factory.SeedBrandAsync();
        var (user, outfit, top, _, _) = await ReadyOutfitAsync(marker, topSize: "L", brandId: brand.Id);
        using var _1 = user;
        using (var size = await user.Client.PutAsJsonAsync("/api/v1/me/sizes", new { brandSlug = brand.Slug, group = "Tops", size = "M" }, Ct))
        {
            Assert.True(size.IsSuccessStatusCode);
        }

        using (var profile = await user.Client.PutAsJsonAsync("/api/v1/me/profile", new { audience = "Women", heightCm = 168, weightKg = 61.5m, waistCm = 70 }, Ct))
        {
            Assert.True(profile.IsSuccessStatusCode);
        }

        var ready = await user.Client.GetTryOnOkAsync($"outfitId={outfit}", Ct);
        Assert.Equal("Ready", ready.Status());
        Assert.Equal("Loose", ready.GetProperty("garments")[0].GetProperty("fitLevel").GetString());
        Assert.Equal(JsonValueKind.Null, ready.GetProperty("garments")[1].GetProperty("fitLevel").ValueKind);

        var spec = FakeVisionClient.LastRenderSpec($"{marker}:top");
        var body = spec.GetProperty("body");
        Assert.Equal(("women", 168, 61.5m, 70), (body.GetProperty("audience").GetString(), body.GetProperty("heightCm").GetInt32(), body.GetProperty("weightKg").GetDecimal(), body.GetProperty("waistCm").GetInt32()));
        var garment = spec.GetProperty("garments")[0];
        Assert.Equal(("top", "L", "M", "Regular"), (garment.GetProperty("family").GetString(), garment.GetProperty("size").GetString(), garment.GetProperty("referenceSize").GetString(), garment.GetProperty("cut").GetString()));
        Assert.Equal(0.5, garment.GetProperty("landmarks").GetProperty("neck").GetProperty("x").GetDouble());
        Assert.Equal(600, spec.GetProperty("width").GetInt32());
        Assert.Equal(top, ready.GetProperty("garments")[0].GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task EachInvalidationOfTheBriefDrawsAgainAndANewBodyDeletesTheOldDrawings()
    {
        var marker = NewMarker();
        var brand = await factory.SeedBrandAsync();
        var (user, outfit, top, _, _) = await ReadyOutfitAsync(marker, topSize: "M", brandId: brand.Id);
        using var _1 = user;

        async Task<int> DrawAsync()
        {
            Assert.Equal("Ready", (await user.Client.GetTryOnOkAsync($"outfitId={outfit}", Ct)).Status());
            return FakeVisionClient.RenderCalls(marker);
        }

        Assert.Equal(1, await DrawAsync());
        Assert.Equal(1, await DrawAsync());
        var firstImage = (await RendersOfAsync(user.Id)).Single().ImageKey!;

        // Measurements: a new key, and the drawing of the old body is deleted with its image.
        using (var profile = await user.Client.PutAsJsonAsync("/api/v1/me/profile", new { heightCm = 181 }, Ct))
        {
            Assert.True(profile.IsSuccessStatusCode);
        }

        Assert.Equal(2, await DrawAsync());
        Assert.Single(await RendersOfAsync(user.Id));
        Assert.Null(factory.Storage.Find(firstImage));

        // A size entry (the reference size).
        using (var size = await user.Client.PutAsJsonAsync("/api/v1/me/sizes", new { brandSlug = brand.Slug, group = "Tops", size = "S" }, Ct))
        {
            Assert.True(size.IsSuccessStatusCode);
        }

        Assert.Equal(3, await DrawAsync());

        // The item's own size.
        using (var patch = await user.Client.PatchItemAsync(top, new { size = "XL" }, Ct))
        {
            Assert.True(patch.IsSuccessStatusCode);
        }

        Assert.Equal(4, await DrawAsync());

        // The garment prepared again (a new source: its category changed) is a new row answer, so a new key.
        using (var patch = await user.Client.PatchItemAsync(top, new { categorySlug = "ust-giyim/tisort" }, Ct))
        {
            Assert.True(patch.IsSuccessStatusCode);
        }

        await factory.RunTryOnAsync(TestClock.UtcNow, items: [top]);
        Assert.Equal(2, FakeVisionClient.PrepareCalls($"{marker}:top"));
        Assert.Equal(5, await DrawAsync());
        Assert.Equal(5, await DrawAsync());
    }

    [Fact]
    public async Task AProductsChangedPhotosArePreparedAgainAndTheOutfitIsDrawnAgain()
    {
        var marker = NewMarker();
        using var user = await factory.CreateUserAsync(Ct);
        var top = await factory.SeedTryOnItemAsync(user.Id, "ust-giyim/gomlek", $"{marker}:top");
        var trousers = await factory.SeedTryOnProductAsync("alt-giyim/jean", [TryOnClientExtensions.ImageUrl(marker, "front", 0), TryOnClientExtensions.ImageUrl(marker, "packshot", 1)]);
        var outfit = await factory.SeedOutfitAsync(user.Id, new SeedPiece(OutfitSlot.Top, top), new SeedPiece(OutfitSlot.Bottom, ProductId: trousers));

        await factory.RunTryOnAsync(TryOnClientExtensions.Noon, items: [top], products: [trousers]);
        var row = await RowOfProductAsync(trousers);
        Assert.Equal((TryOnGarmentStatus.Prepared, "trousers", 1), (row!.Status, row.Family, row.ImageIndex));
        Assert.Equal("Ready", (await user.Client.GetTryOnOkAsync($"outfitId={outfit}", Ct)).Status());

        await factory.WithDbAsync(async db =>
        {
            var image = await db.ProductImages.SingleAsync(image => image.ProductId == trousers && image.SortOrder == 1, Ct);
            image.Url = TryOnClientExtensions.ImageUrl(marker, "new-packshot", 1);
            return await db.SaveChangesAsync(Ct);
        });

        var summary = await factory.RunTryOnAsync(TryOnClientExtensions.Noon.AddMinutes(1), products: [trousers]);
        Assert.Equal(1, summary.CataloguePrepared);
        Assert.Equal(1, FakeVisionClient.PrepareCalls("new-packshot"));
        Assert.NotEqual(row.PreparedAt, (await RowOfProductAsync(trousers))!.PreparedAt);

        Assert.Equal("Ready", (await user.Client.GetTryOnOkAsync($"outfitId={outfit}", Ct)).Status());
        Assert.Equal(2, FakeVisionClient.RenderCalls(marker));
    }

    [Fact]
    public async Task TheReadinessRuleHidesTheActionForEachReasonThroughTheApi()
    {
        var marker = NewMarker();
        using var user = await factory.CreateUserAsync(Ct);
        var shirt = await factory.SeedTryOnItemAsync(user.Id, "ust-giyim/gomlek", $"{marker}:shirt");
        var folded = await factory.SeedTryOnItemAsync(user.Id, "ust-giyim/triko", $"{marker}:folded:tryon-folded");
        var worn = await factory.SeedTryOnItemAsync(user.Id, "ust-giyim/tisort", $"{marker}:worn", warnings: ["person_wearing"]);
        var unreliable = await factory.SeedTryOnItemAsync(user.Id, "alt-giyim/etek", $"{marker}:skirt:tryon-unreliable");
        var trousers = await factory.SeedTryOnItemAsync(user.Id, "alt-giyim/pantolon", $"{marker}:trousers");
        var coat = await factory.SeedTryOnItemAsync(user.Id, "dis-giyim/kaban", $"{marker}:coat");
        var bag = await factory.SeedTryOnItemAsync(user.Id, "aksesuar/canta", $"{marker}:bag");
        var belt = await factory.SeedTryOnItemAsync(user.Id, "aksesuar/kemer", $"{marker}:belt");
        await factory.RunTryOnAsync(TestClock.UtcNow, items: [shirt, folded, worn, unreliable, trousers, coat, bag, belt]);

        // Never prepared: a photo with someone in it.
        Assert.Null(await RowOfItemAsync(worn));
        Assert.Equal(0, FakeVisionClient.PrepareCalls($"{marker}:worn"));

        async Task<(string Status, string? Reason)> AskAsync(params SeedPiece[] pieces)
        {
            var outfit = await factory.SeedOutfitAsync(user.Id, pieces);
            var answer = await user.Client.GetTryOnOkAsync($"outfitId={outfit}", Ct);
            return (answer.Status(), answer.Reason());
        }

        Assert.Equal(("Unavailable", "NoBottom"), await AskAsync(new SeedPiece(OutfitSlot.Top, shirt)));
        Assert.Equal(("Unavailable", "NoTop"), await AskAsync(new SeedPiece(OutfitSlot.Bottom, trousers)));
        Assert.Equal(("Unavailable", "PersonInPhoto"), await AskAsync(new SeedPiece(OutfitSlot.Top, worn), new SeedPiece(OutfitSlot.Bottom, trousers)));
        Assert.Equal(("Unavailable", "PieceNotReady"), await AskAsync(new SeedPiece(OutfitSlot.Top, shirt), new SeedPiece(OutfitSlot.Bottom, unreliable)));
        Assert.Equal(("Unavailable", "FoldedSleeves"), await AskAsync(new SeedPiece(OutfitSlot.Top, folded), new SeedPiece(OutfitSlot.Bottom, trousers)));

        // Under outerwear the folded top is fine; bags and belts (never reliable) never block.
        Assert.Equal(("Ready", null), await AskAsync(
            new SeedPiece(OutfitSlot.Top, folded), new SeedPiece(OutfitSlot.Outerwear, coat), new SeedPiece(OutfitSlot.Bottom, trousers),
            new SeedPiece(OutfitSlot.Accessory, bag), new SeedPiece(OutfitSlot.Accessory, belt)));
        Assert.False((await RowOfItemAsync(bag))!.Reliable);

        // An archived piece is never drawn.
        using (var archive = await user.Client.DeleteAsync($"/api/v1/me/wardrobe/items/{trousers}", Ct))
        {
            Assert.True(archive.IsSuccessStatusCode);
        }

        Assert.Equal(("Unavailable", "PieceNotReady"), await AskAsync(new SeedPiece(OutfitSlot.Top, shirt), new SeedPiece(OutfitSlot.Bottom, trousers)));
    }

    [Fact]
    public async Task ABodyOverTheBmiLimitHidesTheActionAndNoWeightIsTheDefault()
    {
        var (user, outfit, _, _, _) = await ReadyOutfitAsync(NewMarker());
        using var _1 = user;

        // No weight: the default BMI, inside.
        using (var profile = await user.Client.PutAsJsonAsync("/api/v1/me/profile", new { audience = "Men", heightCm = 150 }, Ct))
        {
            Assert.True(profile.IsSuccessStatusCode);
        }

        Assert.Equal("Ready", (await user.Client.GetTryOnOkAsync($"outfitId={outfit}", Ct)).Status());

        // 120 kg at 175 cm: BMI 39.2.
        using (var profile = await user.Client.PutAsJsonAsync("/api/v1/me/profile", new { heightCm = 175, weightKg = 120 }, Ct))
        {
            Assert.True(profile.IsSuccessStatusCode);
        }

        var answer = await user.Client.GetTryOnOkAsync($"outfitId={outfit}", Ct);
        Assert.Equal(("Unavailable", "BodyOutOfRange"), (answer.Status(), answer.Reason()));

        // A weight without a height uses the audience's default height (men 177): 113 kg is BMI 36.07, out.
        using (var profile = await user.Client.PutAsJsonAsync("/api/v1/me/profile", new { heightCm = (int?)null, weightKg = 113 }, Ct))
        {
            Assert.True(profile.IsSuccessStatusCode);
        }

        Assert.Equal("BodyOutOfRange", (await user.Client.GetTryOnOkAsync($"outfitId={outfit}", Ct)).Reason());
    }

    [Theory]
    [InlineData("incomplete_outfit")]
    [InlineData("garment_not_ready")]
    [InlineData("folded_sleeves")]
    [InlineData("body_out_of_range")]
    public async Task EveryRendererRefusalIsUnavailableAndNeverAskedAgain(string code)
    {
        var marker = NewMarker();
        var (user, outfit, _, _, _) = await ReadyOutfitAsync(marker, topFlavor: $"tryon-r422-{code}");
        using var _1 = user;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var answer = await user.Client.GetTryOnOkAsync($"outfitId={outfit}", Ct);
            Assert.Equal(("Unavailable", "RendererRefused"), (answer.Status(), answer.Reason()));
        }

        Assert.Equal(1, FakeVisionClient.RenderCalls(marker));
        var refusal = Assert.Single(await RendersOfAsync(user.Id));
        Assert.Equal((code, (string?)null), (refusal.Refusal, refusal.ImageKey));
    }

    [Fact]
    public async Task ARendererThatIsDownIsA503AndTheNextRequestAsksAgain()
    {
        var marker = NewMarker();
        var (user, outfit, _, _, _) = await ReadyOutfitAsync(marker, topFlavor: "tryon-r503");
        using var _1 = user;

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            using var response = await user.Client.GetAsync($"/api/v1/me/tryon?outfitId={outfit}", Ct);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("TryOn.RendererUnavailable", (await response.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString());
            Assert.Equal(attempt, FakeVisionClient.RenderCalls(marker));
        }

        Assert.Empty(await RendersOfAsync(user.Id));
    }

    [Fact]
    public async Task ABusyPrepareIsTriedOnceMoreLaterThenGivenUpAndA4xxIsNeverRetried()
    {
        var marker = NewMarker();
        using var user = await factory.CreateUserAsync(Ct);
        var busy = await factory.SeedTryOnItemAsync(user.Id, "ust-giyim/gomlek", $"{marker}:busy:tryon-503");
        var refused = await factory.SeedTryOnItemAsync(user.Id, "alt-giyim/jean", $"{marker}:refused:tryon-413");
        var start = TestClock.UtcNow;

        await factory.RunTryOnAsync(start, items: [busy, refused]);
        var waiting = await RowOfItemAsync(busy);
        Assert.Equal((TryOnGarmentStatus.Pending, 1), (waiting!.Status, waiting.Attempts));
        Assert.Equal(start.AddMinutes(10), waiting.NextAttemptAt!.Value, TimeSpan.FromMilliseconds(1));
        Assert.Equal(TryOnGarmentStatus.Refused, (await RowOfItemAsync(refused))!.Status);

        // Before the retry time: nothing. After it: the one retry, which fails too and gives up for a day.
        await factory.RunTryOnAsync(start.AddMinutes(5), items: [busy, refused]);
        Assert.Equal(1, FakeVisionClient.PrepareCalls($"{marker}:busy"));
        await factory.RunTryOnAsync(start.AddMinutes(11), items: [busy, refused]);
        Assert.Equal(2, FakeVisionClient.PrepareCalls($"{marker}:busy"));
        var failed = await RowOfItemAsync(busy);
        Assert.Equal((TryOnGarmentStatus.Failed, 0), (failed!.Status, failed.Attempts));
        await factory.RunTryOnAsync(start.AddMinutes(30), items: [busy, refused]);
        Assert.Equal(2, FakeVisionClient.PrepareCalls($"{marker}:busy"));

        // The refused one was asked exactly once, ever.
        Assert.Equal(1, FakeVisionClient.PrepareCalls($"{marker}:refused"));

        // Both hide the action.
        var outfit = await factory.SeedOutfitAsync(user.Id, new SeedPiece(OutfitSlot.Top, busy), new SeedPiece(OutfitSlot.Bottom, refused));
        Assert.Equal("PieceNotReady", (await user.Client.GetTryOnOkAsync($"outfitId={outfit}", Ct)).Reason());
    }

    [Fact]
    public async Task TheJobIsIdempotentAndTwoRunsAtOnceNeverPrepareAGarmentTwice()
    {
        var marker = NewMarker();
        using var user = await factory.CreateUserAsync(Ct);
        var items = new List<Guid>();
        for (var index = 0; index < 4; index++)
        {
            items.Add(await factory.SeedTryOnItemAsync(user.Id, "ust-giyim/tisort", $"{marker}:item{index}"));
        }

        var products = new List<Guid>();
        for (var index = 0; index < 3; index++)
        {
            products.Add(await factory.SeedTryOnProductAsync("alt-giyim/pantolon", [TryOnClientExtensions.ImageUrl(marker, $"product{index}")]));
        }

        var runs = await Task.WhenAll(
            factory.RunTryOnAsync(TryOnClientExtensions.Night, items, products),
            factory.RunTryOnAsync(TryOnClientExtensions.Night, items, products));
        Assert.Equal(4, runs.Sum(run => run.WardrobePrepared));
        Assert.Equal(3, runs.Sum(run => run.CataloguePrepared));
        Assert.Equal(7, FakeVisionClient.PrepareCalls(marker));

        var again = await factory.RunTryOnAsync(TryOnClientExtensions.Night, items, products);
        Assert.Equal((0, 0), (again.WardrobePrepared, again.CataloguePrepared));
        Assert.Equal(7, FakeVisionClient.PrepareCalls(marker));
        Assert.Equal(7, await factory.WithDbAsync(db => db.TryOnGarments.CountAsync(row => items.Contains(row.WardrobeItemId!.Value) || products.Contains(row.ProductId!.Value), Ct)));
    }

    [Fact]
    public async Task TheCatalogueIsPreparedOneAtATimeAFewByDayAndMoreAtNight()
    {
        var marker = NewMarker();
        var day = new List<Guid>();
        for (var index = 0; index < 7; index++)
        {
            day.Add(await factory.SeedTryOnProductAsync("ust-giyim/tisort", [TryOnClientExtensions.ImageUrl(marker, $"day{index}")]));
        }

        // During the day: five products a run.
        var noon = await factory.RunTryOnAsync(TryOnClientExtensions.Noon, products: day);
        Assert.Equal(5, noon.CataloguePrepared);

        // A second run while the first holds the catalogue lock prepares no product at all.
        var night = new List<Guid>();
        for (var index = 0; index < 7; index++)
        {
            night.Add(await factory.SeedTryOnProductAsync("ust-giyim/tisort", [TryOnClientExtensions.ImageUrl(marker, $"night{index}")]));
        }

        var firstUrl = TryOnClientExtensions.ImageUrl(marker, "night0");
        Task<TryOnPrepareSummary> held;
        using (var gate = FakeCatalogImageHandler.Hold(firstUrl))
        {
            held = factory.RunTryOnAsync(TryOnClientExtensions.Night, products: night);
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);

            var blocked = await factory.RunTryOnAsync(TryOnClientExtensions.Noon, products: day);
            Assert.True(blocked.CatalogueBusy);
            Assert.Equal(0, blocked.CataloguePrepared);
            gate.Open();
        }

        // At night: all seven in one run.
        Assert.Equal(7, (await held).CataloguePrepared);
        Assert.Equal(2, (await factory.RunTryOnAsync(TryOnClientExtensions.Noon, products: day)).CataloguePrepared);
    }

    [Fact]
    public async Task AnotherServiceVersionIsPreparedAgainAfterTheNewWork()
    {
        var marker = NewMarker();
        var old = await factory.SeedTryOnProductAsync("alt-giyim/etek", [TryOnClientExtensions.ImageUrl(marker, "old")]);
        await factory.RunTryOnAsync(TryOnClientExtensions.Noon, products: [old]);
        await factory.WithDbAsync(db => db.TryOnGarments.Where(row => row.ProductId == old)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.PrepareVersion, "prepare-v5").SetProperty(row => row.PreparedAt, TryOnClientExtensions.Noon.AddDays(-30)), Ct));

        var fresh = new List<Guid>();
        for (var index = 0; index < 5; index++)
        {
            fresh.Add(await factory.SeedTryOnProductAsync("alt-giyim/etek", [TryOnClientExtensions.ImageUrl(marker, $"new{index}")]));
        }

        // Five new products fill the day's batch first; the version re-prepare waits for the next run.
        var first = await factory.RunTryOnAsync(TryOnClientExtensions.Noon, products: [old, .. fresh]);
        Assert.Equal(5, first.CataloguePrepared);
        Assert.Equal("prepare-v5", (await RowOfProductAsync(old))!.PrepareVersion);

        var second = await factory.RunTryOnAsync(TryOnClientExtensions.Noon, products: [old, .. fresh]);
        Assert.Equal(1, second.CataloguePrepared);
        Assert.Equal(FakeVisionClient.PrepareVersion, (await RowOfProductAsync(old))!.PrepareVersion);
    }

    [Fact]
    public async Task NewDrawingsAreLimitedPerHourAndOneAtATime()
    {
        var marker = NewMarker();
        var (user, outfit, _, _, _) = await ReadyOutfitAsync(marker);
        using var _1 = user;

        // One in flight: while a drawing of the caller runs, another request answers Preparing.
        var hold = FakeVisionClient.HoldRender($"{marker}:top");
        var running = user.Client.GetTryOnOkAsync($"outfitId={outfit}", Ct);
        await hold.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        using (var profile = await user.Client.PutAsJsonAsync("/api/v1/me/profile", new { heightCm = 150 }, Ct))
        {
            Assert.True(profile.IsSuccessStatusCode);
        }

        Assert.Equal("Preparing", (await user.Client.GetTryOnOkAsync($"outfitId={outfit}", Ct)).Status());
        FakeVisionClient.ReleaseRender($"{marker}:top");
        Assert.Equal("Ready", (await running).Status());

        // 30 new drawings an hour (the two above count: one drew, one is the next height's); cache hits are free.
        for (var height = 151; FakeVisionClient.RenderCalls(marker) < 30; height++)
        {
            using var profile = await user.Client.PutAsJsonAsync("/api/v1/me/profile", new { heightCm = height }, Ct);
            Assert.Equal("Ready", (await user.Client.GetTryOnOkAsync($"outfitId={outfit}", Ct)).Status());
            Assert.Equal("Ready", (await user.Client.GetTryOnOkAsync($"outfitId={outfit}", Ct)).Status());
        }

        using (var profile = await user.Client.PutAsJsonAsync("/api/v1/me/profile", new { heightCm = 199 }, Ct))
        {
            Assert.True(profile.IsSuccessStatusCode);
        }

        using var limited = await user.Client.GetAsync($"/api/v1/me/tryon?outfitId={outfit}", Ct);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        var problem = await limited.ReadProblemAsync(Ct);
        Assert.Equal("RateLimited", problem.GetProperty("errorCode").GetString());
        Assert.InRange(problem.GetProperty("retryAfterSeconds").GetInt32(), 1, 3600);
        Assert.NotNull(limited.Headers.RetryAfter);
        Assert.Equal(30, FakeVisionClient.RenderCalls(marker));
    }

    [Fact]
    public async Task DolabimlaDeneDrawsTheProductWithTheCallersOwnPiecesOnlyAndAloneItIsUnavailable()
    {
        var marker = NewMarker();
        using var user = await factory.CreateUserAsync(Ct);
        var shirt = await factory.SeedTryOnProductAsync("ust-giyim/gomlek", [TryOnClientExtensions.ImageUrl(marker, "shirt")], color: ProductColor.White);

        // Nothing to wear it with: the product alone, never bare legs.
        var alone = await user.Client.GetTryOnOkAsync($"productId={shirt}&withWardrobe=true", Ct);
        Assert.Equal(("Unavailable", "NoBottom"), (alone.Status(), alone.Reason()));
        Assert.Equal("NoBottom", (await user.Client.GetTryOnOkAsync($"productId={shirt}", Ct)).Reason());

        var trousers = await factory.SeedTryOnItemAsync(user.Id, "alt-giyim/pantolon", $"{marker}:trousers", color: ProductColor.Navy);
        var shoes = await factory.SeedTryOnItemAsync(user.Id, "ayakkabi/sneaker", $"{marker}:shoes", color: ProductColor.White);
        var withWardrobe = await user.Client.GetTryOnOkAsync($"productId={shirt}&withWardrobe=true", Ct);
        Assert.Equal("Preparing", withWardrobe.Status());

        await factory.RunTryOnAsync(TryOnClientExtensions.Noon, items: [trousers, shoes], products: [shirt]);
        var ready = await user.Client.GetTryOnOkAsync($"productId={shirt}&withWardrobe=true", Ct);
        Assert.Equal("Ready", ready.Status());
        var drawn = ready.GetProperty("garments").EnumerateArray().Select(garment => garment.GetProperty("id").GetGuid()).ToList();
        Assert.Contains(shirt, drawn);
        Assert.Contains(trousers, drawn);
    }

    [Fact]
    public async Task ACheckSaysWhetherTheDrawingWouldBeAvailableWithoutDrawingOrSpendingTheLimit()
    {
        var marker = NewMarker();
        using var user = await factory.CreateUserAsync(Ct);
        var (outfit, top, trousers, shoes) = await factory.SeedWardrobeOutfitAsync(user.Id, marker);

        // Not prepared yet: Preparing, and the prepare is asked for (otherwise the answer could never change); nothing is drawn.
        var preparing = await user.Client.GetTryOnOkAsync($"outfitId={outfit}&check=true", Ct);
        Assert.Equal("Preparing", preparing.Status());
        Assert.Equal(JsonValueKind.Null, preparing.GetProperty("imageUrl").ValueKind);
        Assert.Equal(TryOnGarmentStatus.Pending, (await RowOfItemAsync(top))!.Status);
        await factory.RunTryOnAsync(TestClock.UtcNow, items: [top, trousers, shoes]);

        // Ready means a real request would draw: the shape is the same record, with no image, no garments and no renderer; nothing was drawn or stored.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var check = await user.Client.GetTryOnOkAsync($"outfitId={outfit}&check=true", Ct);
            Assert.Equal("Ready", check.Status());
            Assert.Null(check.Reason());
            Assert.Equal(JsonValueKind.Null, check.GetProperty("imageUrl").ValueKind);
            Assert.Empty(check.GetProperty("garments").EnumerateArray());
            Assert.Empty(check.GetProperty("defaultedMeasurements").EnumerateArray());
            Assert.Equal(JsonValueKind.Null, check.GetProperty("renderer").ValueKind);
        }

        Assert.Equal(0, FakeVisionClient.RenderCalls(marker));
        Assert.Empty(await RendersOfAsync(user.Id));

        // The real request still draws, and `check=false` is the plain request.
        var drawn = await user.Client.GetTryOnOkAsync($"outfitId={outfit}&check=false", Ct);
        Assert.Equal("Ready", drawn.Status());
        Assert.False(string.IsNullOrEmpty(drawn.GetProperty("imageUrl").GetString()));
        Assert.Equal(1, FakeVisionClient.RenderCalls(marker));

        // A check is a cache read: it does not move the render's last use.
        var before = Assert.Single(await RendersOfAsync(user.Id)).LastUsedAt;
        Assert.Equal("Ready", (await user.Client.GetTryOnOkAsync($"outfitId={outfit}&check=true", Ct)).Status());
        Assert.Equal(before, Assert.Single(await RendersOfAsync(user.Id)).LastUsedAt);
        Assert.Equal(1, FakeVisionClient.RenderCalls(marker));
    }

    [Fact]
    public async Task ACheckAnswersWhatTheReadinessRuleAndARememberedRefusalWouldAnswerAndNeverSpendsTheLimit()
    {
        // The readiness rule: a product alone can never be drawn.
        var marker = NewMarker();
        using var alone = await factory.CreateUserAsync(Ct);
        var product = await factory.SeedTryOnProductAsync("ust-giyim/gomlek", [TryOnClientExtensions.ImageUrl(marker)]);
        var unavailable = await alone.Client.GetTryOnOkAsync($"productId={product}&check=true", Ct);
        Assert.Equal("Unavailable", unavailable.Status());
        Assert.NotNull(unavailable.Reason());

        // A refusal the renderer already gave for these inputs: the check knows it without asking again.
        var refusedMarker = NewMarker();
        var (user, outfit, _, _, _) = await ReadyOutfitAsync(refusedMarker, topFlavor: "tryon-r422-folded_sleeves");
        using var _1 = user;
        Assert.Equal("Ready", (await user.Client.GetTryOnOkAsync($"outfitId={outfit}&check=true", Ct)).Status());
        Assert.Equal("Unavailable", (await user.Client.GetTryOnOkAsync($"outfitId={outfit}", Ct)).Status());
        var refused = await user.Client.GetTryOnOkAsync($"outfitId={outfit}&check=true", Ct);
        Assert.Equal(("Unavailable", "RendererRefused"), (refused.Status(), refused.Reason()));
        Assert.Equal(1, FakeVisionClient.RenderCalls(refusedMarker));

        // Checks are free: more than the hourly number of them are never limited.
        for (var attempt = 0; attempt < 40; attempt++)
        {
            Assert.Equal("Unavailable", (await user.Client.GetTryOnOkAsync($"outfitId={outfit}&check=true", Ct)).Status());
        }

        Assert.Equal(1, FakeVisionClient.RenderCalls(refusedMarker));
    }

    [Fact]
    public async Task TheReadyAnswerNamesTheRendererTheReportSendsBack()
    {
        var marker = NewMarker();
        var (user, outfit, top, _, _) = await ReadyOutfitAsync(marker);
        using var _1 = user;

        var ready = await user.Client.GetTryOnOkAsync($"outfitId={outfit}", Ct);
        var renderer = ready.GetProperty("renderer").GetString();
        Assert.Equal(FakeVisionClient.Renderer, renderer);
        Assert.Equal(Assert.Single(await RendersOfAsync(user.Id)).Renderer, renderer);

        // A cache hit names it too; a Preparing or Unavailable answer has none.
        Assert.Equal(renderer, (await user.Client.GetTryOnOkAsync($"outfitId={outfit}", Ct)).GetProperty("renderer").GetString());

        using var reported = await user.Client.PostAsJsonAsync("/api/v1/me/tryon/reports", new { outfitId = outfit, garmentIds = new[] { top }, renderer }, Ct);
        Assert.Equal(HttpStatusCode.NoContent, reported.StatusCode);
        Assert.Equal(renderer, Assert.Single(await factory.WithDbAsync(db => db.TryOnReports.AsNoTracking().Where(row => row.UserId == user.Id).ToListAsync(Ct))).Renderer);
    }

    [Fact]
    public async Task AReportIsStoredOnceForTheDrawnGarmentsAndTheMetricListsThem()
    {
        var marker = NewMarker();
        var (user, outfit, top, trousers, _) = await ReadyOutfitAsync(marker);
        using var _1 = user;
        using var other = await factory.CreateUserAsync(Ct);
        Assert.Equal("Ready", (await user.Client.GetTryOnOkAsync($"outfitId={outfit}", Ct)).Status());

        var body = new { outfitId = outfit, garmentIds = new[] { top, trousers }, renderer = FakeVisionClient.Renderer };
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var reported = await user.Client.PostAsJsonAsync("/api/v1/me/tryon/reports", body, Ct);
            Assert.Equal(HttpStatusCode.NoContent, reported.StatusCode);
        }

        var report = Assert.Single(await factory.WithDbAsync(db => db.TryOnReports.AsNoTracking().Where(row => row.UserId == user.Id).ToListAsync(Ct)));
        var topRow = await RowOfItemAsync(top);
        var trousersRow = await RowOfItemAsync(trousers);
        Assert.Equal(new[] { topRow!.Id, trousersRow!.Id }.Order(), report.GarmentIds.Order());

        // Not a piece of that outfit: 400. Someone else's outfit: 404.
        using (var unknown = await user.Client.PostAsJsonAsync("/api/v1/me/tryon/reports", new { outfitId = outfit, garmentIds = new[] { Guid.NewGuid() }, renderer = "mannequin-v5" }, Ct))
        {
            Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
            Assert.Equal("TryOn.UnknownGarment", (await unknown.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString());
        }

        using (var foreign = await other.Client.PostAsJsonAsync("/api/v1/me/tryon/reports", body, Ct))
        {
            Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        }

        using (var invalid = await user.Client.PostAsJsonAsync("/api/v1/me/tryon/reports", new { outfitId = outfit, productId = Guid.NewGuid(), garmentIds = Array.Empty<Guid>(), renderer = "" }, Ct))
        {
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }

        // docs/metrics.sql, block tryon_reported_garments: the operators' list, the reported garments with their counts.
        var sql = TryOnMetricSql();
        await using var connection = await factory.OpenConnectionAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("day_start", DateOnly.FromDateTime(TestClock.UtcNow.UtcDateTime.AddDays(-2)));
        command.Parameters.AddWithValue("day_end", DateOnly.FromDateTime(TestClock.UtcNow.UtcDateTime.AddDays(2)));
        var listed = new Dictionary<Guid, (string Kind, long Reports)>();
        await using (var reader = await command.ExecuteReaderAsync(Ct))
        {
            while (await reader.ReadAsync(Ct))
            {
                listed[reader.GetGuid(0)] = (reader.GetString(1), reader.GetInt64(reader.GetOrdinal("reports")));
            }
        }

        Assert.Equal(("wardrobe", 1L), listed[topRow.Id]);
        Assert.Equal(("wardrobe", 1L), listed[trousersRow.Id]);
    }

    private static string TryOnMetricSql()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "docs", "metrics.sql");
            if (File.Exists(candidate))
            {
                var file = File.ReadAllText(candidate).ReplaceLineEndings("\n");
                var start = file.IndexOf("-- tryon: tryon_reported_garments", StringComparison.Ordinal);
                Assert.True(start >= 0, "docs/metrics.sql has no tryon_reported_garments block.");
                var block = file[start..].Split('\n', 2)[1];
                var end = block.IndexOf("\n-- ", StringComparison.Ordinal);

                return System.Text.RegularExpressions.Regex.Replace(end < 0 ? block : block[..end], @"(?<![:\w]):(day_start|day_end)\b", "@$1");
            }
        }

        throw new FileNotFoundException("docs/metrics.sql was not found above the test binaries.");
    }

    [Fact]
    public async Task AnotherUsersOutfitIsNotFoundAndTheQueryNeedsOneSubject()
    {
        var (owner, outfit, _, _, _) = await ReadyOutfitAsync(NewMarker());
        using var _1 = owner;
        using var other = await factory.CreateUserAsync(Ct);

        using (var foreign = await other.Client.GetAsync($"/api/v1/me/tryon?outfitId={outfit}", Ct))
        {
            Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
            Assert.Equal("TryOn.OutfitNotFound", (await foreign.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString());
        }

        Assert.Empty(await RendersOfAsync(other.Id));

        foreach (var query in new[] { string.Empty, $"outfitId={outfit}&productId={Guid.NewGuid()}", $"outfitId={outfit}&withWardrobe=true" })
        {
            using var invalid = await other.Client.GetAsync($"/api/v1/me/tryon?{query}", Ct);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }

        using var missing = await other.Client.GetAsync($"/api/v1/me/tryon?productId={Guid.NewGuid()}", Ct);
        Assert.Equal("TryOn.ProductNotFound", (await missing.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task SwitchedOffBothRoutesAre404AndTheJobDoesNothing()
    {
        await using var off = factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection([new KeyValuePair<string, string?>("TryOn:Enabled", "false")])));
        var marker = NewMarker();
        using var user = await factory.CreateUserAsync(Ct);
        var item = await factory.SeedTryOnItemAsync(user.Id, "ust-giyim/gomlek", $"{marker}:top");
        using var client = off.CreateClient();
        client.DefaultRequestHeaders.Authorization = user.Client.DefaultRequestHeaders.Authorization;

        using (var get = await client.GetAsync($"/api/v1/me/tryon?outfitId={Guid.NewGuid()}", Ct))
        {
            Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
            Assert.Equal("TryOn.Disabled", (await get.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString());
        }

        using (var report = await client.PostAsJsonAsync("/api/v1/me/tryon/reports", new { outfitId = Guid.NewGuid(), garmentIds = new[] { item }, renderer = "x" }, Ct))
        {
            Assert.Equal("TryOn.Disabled", (await report.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString());
        }

        await using var scope = off.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<TryOnPrepareJob>().RunAsync(Ct);
        Assert.Equal(0, FakeVisionClient.PrepareCalls(marker));
    }
}
