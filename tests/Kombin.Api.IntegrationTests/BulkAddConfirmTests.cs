using System.Net;
using System.Text.Json;
using Kombin.Api.Common.Storage;
using Kombin.Api.Features.Wardrobe;
using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Wardrobe;
using Microsoft.EntityFrameworkCore;

namespace Kombin.Api.IntegrationTests;

/// <summary>
/// <c>POST /me/wardrobe/bulk/{id}/confirm</c> and <c>DELETE /me/wardrobe/bulk/{id}</c> (task BA4, PR 3; <c>docs/design/bulk-add.md</c> sections a, b, e):
/// the ticked pieces become ordinary wardrobe items under their own IDs, everything else of the batch is gone.
/// </summary>
public sealed class BulkAddConfirmTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] ClearSecondary = ["secondaryColor"];

    private static readonly string[] ClearName = ["name"];

    private sealed record Ready(TestUser User, Guid BatchId, List<BulkPiece> Pieces, DateTimeOffset Now, string PhotoText);

    /// <summary>A new user with an analysed batch, every request pinned at the same instant.</summary>
    private async Task<Ready> ReadyAsync(string flavor = "bulk-3")
    {
        var user = await factory.CreateUserAsync(Ct);
        var now = BulkAddClientExtensions.PinnedNow();
        user.Client.At(now);
        var started = await factory.RunBulkAsync(user.Client, flavor, Ct);

        return new Ready(user, started.Id, await factory.BulkPieceRowsAsync(started.Id, Ct), now, started.PhotoText);
    }

    private Task<List<WardrobeItem>> ItemsAsync(IEnumerable<Guid> ids)
    {
        var list = ids.ToList();
        return factory.WithDbAsync(db => db.WardrobeItems.AsNoTracking().Where(item => list.Contains(item.Id)).ToListAsync(Ct));
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string errorCode)
    {
        Assert.True(response.StatusCode == status, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");
        Assert.Equal(errorCode, (await response.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString());
    }

    private static string ItemKey(Guid userId, Guid itemId, ImageVariant variant, string extension) =>
        StorageKeys.Build(WardrobeLimits.StorageArea, userId, itemId, variant, extension);

    private List<string> KeysUnder(Guid userId, string prefix) => [.. factory.Storage.KeysOfUser(userId).Where(key => key.StartsWith(prefix, StringComparison.Ordinal))];

    // ---- The item is a single add's item ----

    [Fact]
    public async Task TheItemCarriesThePiecesAnalysedAttributes()
    {
        var ready = await ReadyAsync();
        using var _ = ready.User;
        var piece = ready.Pieces[0];

        await ready.User.Client.BulkConfirmAsync(ready.BatchId, [piece.Id], Ct);

        var item = Assert.Single(await ItemsAsync([piece.Id]));
        Assert.Equal(ready.User.Id, item.UserId);
        Assert.Equal(piece.CategoryId, item.CategoryId);
        Assert.Equal(piece.PrimaryColor, item.PrimaryColor);
        Assert.Equal(piece.SecondaryColor, item.SecondaryColor);
        Assert.Equal(piece.Pattern, item.Pattern);
        Assert.Equal(piece.Formality, item.Formality);
        Assert.Equal(piece.Seasons, item.Seasons);
        Assert.Equal(piece.Warnings, item.Warnings);
        Assert.Equal(piece.Embedding!.ToArray(), item.Embedding!.ToArray());
        Assert.Equal(piece.EmbeddingModel, item.EmbeddingModel);
        Assert.Equal(piece.AnalysisJson, item.AnalysisJson);
        Assert.False(item.FromCatalog);
        Assert.False(item.IsArchived);
    }

    [Fact]
    public async Task TheItemIsAReadyItemStampedWithTheConfirmTime()
    {
        var ready = await ReadyAsync();
        using var _ = ready.User;
        var confirmAt = ready.Now.AddMinutes(3);
        ready.User.Client.At(confirmAt);

        await ready.User.Client.BulkConfirmAsync(ready.BatchId, [ready.Pieces[0].Id], Ct);

        var item = Assert.Single(await ItemsAsync([ready.Pieces[0].Id]));
        Assert.Equal(WardrobeItemStatus.Ready, item.Status);
        Assert.Equal(confirmAt, item.ConfirmedAt);
        Assert.Equal(confirmAt, item.CreatedAt);
    }

    [Fact]
    public async Task TheItemsImagesAreTheStagedOnesUnderTheSingleAddsKeys()
    {
        var ready = await ReadyAsync();
        using var _ = ready.User;
        var piece = ready.Pieces[1];
        var photoText = ready.PhotoText;

        await ready.User.Client.BulkConfirmAsync(ready.BatchId, [piece.Id], Ct);

        var item = Assert.Single(await ItemsAsync([piece.Id]));
        Assert.Equal(ItemKey(ready.User.Id, piece.Id, ImageVariant.Cutout, "png"), item.ImageCutoutKey);
        Assert.Equal(ItemKey(ready.User.Id, piece.Id, ImageVariant.Thumb, "webp"), item.ImageThumbKey);
        var cutout = factory.Storage.Find(item.ImageCutoutKey!);
        var thumb = factory.Storage.Find(item.ImageThumbKey!);
        Assert.NotNull(cutout);
        Assert.NotNull(thumb);
        Assert.Equal(FakeVisionClient.BulkCutout(photoText, 1), cutout.Content);
        Assert.Equal(FakeVisionClient.BulkThumbnail(photoText, 1), thumb.Content);
        Assert.Equal("image/png", cutout.ContentType);
        Assert.Equal("image/webp", thumb.ContentType);
    }

    [Fact]
    public async Task TheItemsIdIsThePiecesId()
    {
        var ready = await ReadyAsync();
        using var _ = ready.User;

        var answer = await ready.User.Client.BulkConfirmAsync(ready.BatchId, [ready.Pieces[0].Id, ready.Pieces[2].Id], Ct);

        Assert.Equal([ready.Pieces[0].Id, ready.Pieces[2].Id], answer.ItemIds());
        Assert.Equal(2, (await ItemsAsync([ready.Pieces[0].Id, ready.Pieces[2].Id])).Count);
    }

    [Fact]
    public async Task TheAnswerIsConfirmedWithTheItemsInReadingOrder()
    {
        var ready = await ReadyAsync();
        using var _ = ready.User;

        var answer = await ready.User.Client.BulkConfirmAsync(ready.BatchId, [ready.Pieces[2].Id, ready.Pieces[0].Id], Ct);

        Assert.Equal(ready.BatchId, answer.GetProperty("id").GetGuid());
        Assert.Equal("Confirmed", answer.GetProperty("status").GetString());
        Assert.Equal([ready.Pieces[0].Id, ready.Pieces[2].Id], answer.ItemIds());
        Assert.All(answer.GetProperty("items").EnumerateArray(), item => Assert.Equal("Ready", item.GetProperty("status").GetString()));
    }

    [Fact]
    public async Task TheStatusAnswerAfterConfirmEqualsTheConfirmAnswer()
    {
        var ready = await ReadyAsync();
        using var _ = ready.User;

        var answer = await ready.User.Client.BulkConfirmAsync(ready.BatchId, [ready.Pieces[0].Id, ready.Pieces[1].Id], Ct);
        var status = await ready.User.Client.BulkStatusAsync(ready.BatchId, Ct);

        Assert.Equal(answer.GetRawText(), status.GetRawText());
    }

    [Fact]
    public async Task TheBatchRowRemembersTheConfirm()
    {
        var ready = await ReadyAsync();
        using var _ = ready.User;

        await ready.User.Client.BulkConfirmAsync(ready.BatchId, [ready.Pieces[1].Id, ready.Pieces[0].Id], Ct);

        var batch = await factory.BulkBatchRowAsync(ready.BatchId, Ct);
        Assert.Equal(BulkBatchStatus.Confirmed, batch.Status);
        Assert.Equal(ready.Now, batch.ConfirmedAt);
        Assert.Equal(ready.Now, batch.EndedAt);
        Assert.Equal([ready.Pieces[0].Id, ready.Pieces[1].Id], batch.ConfirmedItemIds);
        Assert.NotNull(batch.ObjectsDeletedAt);
        Assert.Null(batch.OriginalKey);
    }

    // ---- Edits: PATCH's own fields and rules ----

    [Fact]
    public async Task ACategoryEditSetsTheCategory()
    {
        var ready = await ReadyAsync();
        using var _ = ready.User;
        var piece = ready.Pieces[0];
        var pantolon = await CategoryIdAsync("alt-giyim/pantolon");

        using var response = await ready.User.Client.BulkConfirmRawAsync(ready.BatchId, new { pieces = new[] { new { id = piece.Id, categorySlug = "alt-giyim/pantolon" } } }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(pantolon, Assert.Single(await ItemsAsync([piece.Id])).CategoryId);
    }

    [Fact]
    public async Task ACategoryEditKeepsFormalityAndSeasonsLikePatch()
    {
        var ready = await ReadyAsync();
        using var _ = ready.User;
        var piece = ready.Pieces[0];

        using var response = await ready.User.Client.BulkConfirmRawAsync(ready.BatchId, new { pieces = new[] { new { id = piece.Id, categorySlug = "ayakkabi/sneaker" } } }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var item = Assert.Single(await ItemsAsync([piece.Id]));
        Assert.Equal((piece.Formality, piece.Seasons), (item.Formality, item.Seasons));
    }

    private Task<Guid> CategoryIdAsync(string slug) =>
        factory.WithDbAsync(db => db.Categories.Where(category => category.Slug == slug).Select(category => category.Id).SingleAsync(Ct));

    [Fact]
    public async Task AColourEditSetsBothColours()
    {
        var ready = await ReadyAsync();
        using var _ = ready.User;
        var piece = ready.Pieces[0];

        using var response = await ready.User.Client.BulkConfirmRawAsync(
            ready.BatchId, new { pieces = new[] { new { id = piece.Id, primaryColor = "Red", secondaryColor = "White" } } }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var item = Assert.Single(await ItemsAsync([piece.Id]));
        Assert.Equal((ProductColor.Red, (ProductColor?)ProductColor.White), (item.PrimaryColor, item.SecondaryColor));
    }

    [Fact]
    public async Task ClearRemovesTheSecondaryColour()
    {
        var ready = await ReadyAsync("bulk-single");
        using var _ = ready.User;
        var piece = Assert.Single(ready.Pieces);
        Assert.NotNull(piece.SecondaryColor);

        using var response = await ready.User.Client.BulkConfirmRawAsync(ready.BatchId, new { pieces = new[] { new { id = piece.Id, clear = ClearSecondary } } }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(Assert.Single(await ItemsAsync([piece.Id])).SecondaryColor);
    }

    [Fact]
    public async Task TheCountersRecordTheStopRuleNumbers()
    {
        var ready = await ReadyAsync();
        using var _ = ready.User;

        // Pre-ticked: pieces 0 and 1. Ticked: 0 (category changed) and 2 (primary colour changed).
        using var response = await ready.User.Client.BulkConfirmRawAsync(
            ready.BatchId,
            new { pieces = new object[] { new { id = ready.Pieces[0].Id, categorySlug = "alt-giyim/pantolon" }, new { id = ready.Pieces[2].Id, primaryColor = "Red" } } },
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var batch = await factory.BulkBatchRowAsync(ready.BatchId, Ct);
        Assert.Equal((3, 2), (batch.PiecesShown, batch.PiecesPreTicked));
        Assert.Equal((2, 1), (batch.PiecesTicked, batch.TickedPreTicked));
        Assert.Equal((1, 1), (batch.CategoryEdits, batch.ColourEdits));
    }

    // ---- Idempotence and races ----

    [Fact]
    public async Task ASecondConfirmAnswersTheStoredResult()
    {
        var ready = await ReadyAsync();
        using var _ = ready.User;
        var first = await ready.User.Client.BulkConfirmAsync(ready.BatchId, [ready.Pieces[0].Id, ready.Pieces[1].Id], Ct);

        var second = await ready.User.Client.BulkConfirmAsync(ready.BatchId, [ready.Pieces[2].Id], Ct);

        Assert.Equal(first.ItemIds(), second.ItemIds());
        Assert.Equal("Confirmed", second.GetProperty("status").GetString());
        Assert.Empty(await ItemsAsync([ready.Pieces[2].Id]));
    }

    [Fact]
    public async Task TwoConcurrentConfirmsBothAnswer200WithOneSetOfItems()
    {
        var ready = await ReadyAsync();
        using var _ = ready.User;
        Guid[] ticked = [ready.Pieces[0].Id, ready.Pieces[1].Id];

        var responses = await Task.WhenAll(
            ready.User.Client.BulkConfirmRawAsync(ready.BatchId, ticked, Ct),
            ready.User.Client.BulkConfirmRawAsync(ready.BatchId, ticked, Ct));

        try
        {
            Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
            var bodies = await Task.WhenAll(responses.Select(response => response.Content.ReadAsStringAsync(Ct)));
            Assert.All(bodies, body => Assert.Equal(ticked, JsonDocument.Parse(body).RootElement.ItemIds()));
            Assert.Equal(2, await factory.WithDbAsync(db => db.WardrobeItems.CountAsync(item => item.UserId == ready.User.Id, Ct)));
        }
        finally
        {
            Array.ForEach(responses, response => response.Dispose());
        }
    }

    [Fact]
    public async Task ConfirmRacingCancelEndsInExactlyOneConsistentOutcome()
    {
        var ready = await ReadyAsync();
        using var _ = ready.User;
        Guid[] ticked = [ready.Pieces[0].Id, ready.Pieces[1].Id];

        var confirm = ready.User.Client.BulkConfirmRawAsync(ready.BatchId, ticked, Ct);
        var cancel = ready.User.Client.BulkCancelAsync(ready.BatchId, Ct);
        await Task.WhenAll(confirm, cancel);
        using var confirmed = await confirm;
        using var cancelled = await cancel;

        var batch = await factory.BulkBatchRowAsync(ready.BatchId, Ct);
        var items = await ItemsAsync(ticked);
        if (confirmed.StatusCode == HttpStatusCode.OK)
        {
            await AssertProblemAsync(cancelled, HttpStatusCode.Conflict, "Wardrobe.BulkAlreadyConfirmed");
            Assert.Equal(BulkBatchStatus.Confirmed, batch.Status);
            Assert.Equal(2, items.Count);
        }
        else
        {
            Assert.Equal(HttpStatusCode.NoContent, cancelled.StatusCode);
            await AssertProblemAsync(confirmed, HttpStatusCode.Conflict, "Wardrobe.BulkExpired");
            Assert.Equal(BulkBatchStatus.Cancelled, batch.Status);
            Assert.Empty(items);
            Assert.Empty(KeysUnder(ready.User.Id, $"{WardrobeLimits.StorageArea}/"));
        }

        Assert.Empty(await factory.BulkPieceRowsAsync(ready.BatchId, Ct));
        Assert.Empty(KeysUnder(ready.User.Id, StorageKeys.BulkBatchPrefix(ready.User.Id, ready.BatchId)));
    }

    // ---- Storage failure ----

    /// <summary>A batch whose confirm of pieces 0 and 1 meets a store that refuses piece 1's thumbnail (so some copies were already written).</summary>
    private async Task<(Ready Ready, Guid[] Ticked, string FailureName, HttpResponseMessage Response)> FailedConfirmAsync()
    {
        var ready = await ReadyAsync();
        Guid[] ticked = [ready.Pieces[0].Id, ready.Pieces[1].Id];
        var failing = ItemKey(ready.User.Id, ready.Pieces[1].Id, ImageVariant.Thumb, "webp");
        var name = $"bulk-confirm-{ready.BatchId:N}";
        factory.Storage.FailPuts(name, key => key == failing);

        var response = await ready.User.Client.BulkConfirmRawAsync(ready.BatchId, ticked, Ct);

        return (ready, ticked, name, response);
    }

    [Fact]
    public async Task AStorageFailureAnswers503StorageUnavailable()
    {
        var (ready, _, name, response) = await FailedConfirmAsync();
        using var _1 = ready.User;
        using var _2 = response;
        factory.Storage.StopFailingPuts(name);

        await AssertProblemAsync(response, HttpStatusCode.ServiceUnavailable, "Wardrobe.StorageUnavailable");
    }

    [Fact]
    public async Task AStorageFailureWritesNoItemAndKeepsTheBatchReady()
    {
        var (ready, ticked, name, response) = await FailedConfirmAsync();
        using var _1 = ready.User;
        using var _2 = response;
        factory.Storage.StopFailingPuts(name);

        Assert.Empty(await ItemsAsync(ticked));
        Assert.Equal(BulkBatchStatus.Ready, (await factory.BulkBatchRowAsync(ready.BatchId, Ct)).Status);
        Assert.Equal(3, (await factory.BulkPieceRowsAsync(ready.BatchId, Ct)).Count);
    }

    [Fact]
    public async Task AStorageFailureLeavesNoItemObject()
    {
        var (ready, _, name, response) = await FailedConfirmAsync();
        using var _1 = ready.User;
        using var _2 = response;
        factory.Storage.StopFailingPuts(name);

        Assert.Empty(KeysUnder(ready.User.Id, $"{WardrobeLimits.StorageArea}/"));
    }

    [Fact]
    public async Task ARetryAfterAStorageFailureSucceeds()
    {
        var (ready, ticked, name, response) = await FailedConfirmAsync();
        using var _1 = ready.User;
        using var _2 = response;
        factory.Storage.StopFailingPuts(name);

        var answer = await ready.User.Client.BulkConfirmAsync(ready.BatchId, ticked, Ct);

        Assert.Equal(ticked, answer.ItemIds());
        Assert.Equal(2, (await ItemsAsync(ticked)).Count);
        Assert.All(ticked, id => Assert.NotNull(factory.Storage.Find(ItemKey(ready.User.Id, id, ImageVariant.Thumb, "webp"))));
    }

    // ---- What is left after a confirm ----

    [Fact]
    public async Task AnUntickedPieceBecomesNoItem()
    {
        var ready = await ReadyAsync();
        using var _ = ready.User;

        await ready.User.Client.BulkConfirmAsync(ready.BatchId, [ready.Pieces[0].Id], Ct);

        Assert.Empty(await ItemsAsync([ready.Pieces[1].Id, ready.Pieces[2].Id]));
        Assert.Equal(1, await factory.WithDbAsync(db => db.WardrobeItems.CountAsync(item => item.UserId == ready.User.Id, Ct)));
    }

    [Fact]
    public async Task NoPieceRowIsLeftAfterConfirm()
    {
        var ready = await ReadyAsync();
        using var _ = ready.User;

        await ready.User.Client.BulkConfirmAsync(ready.BatchId, [ready.Pieces[0].Id], Ct);

        Assert.Empty(await factory.BulkPieceRowsAsync(ready.BatchId, Ct));
    }

    [Fact]
    public async Task NoObjectIsLeftUnderTheBatchPrefixAfterConfirm()
    {
        var ready = await ReadyAsync();
        using var _ = ready.User;
        var prefix = StorageKeys.BulkBatchPrefix(ready.User.Id, ready.BatchId);
        Assert.NotEmpty(KeysUnder(ready.User.Id, prefix));

        await ready.User.Client.BulkConfirmAsync(ready.BatchId, [ready.Pieces[0].Id], Ct);

        Assert.Empty(KeysUnder(ready.User.Id, prefix));
    }

    [Fact]
    public async Task EveryObjectOfAConfirmedItemHasAPublicName()
    {
        var ready = await ReadyAsync();
        using var _ = ready.User;
        Guid[] ticked = [ready.Pieces[0].Id, ready.Pieces[1].Id];

        await ready.User.Client.BulkConfirmAsync(ready.BatchId, ticked, Ct);

        foreach (var id in ticked)
        {
            var keys = KeysUnder(ready.User.Id, $"{WardrobeLimits.StorageArea}/{ready.User.Id:D}/{id:D}/");
            Assert.Equal(2, keys.Count);
            Assert.All(keys, key => Assert.True(StorageKeys.IsPublic(key), key));
        }
    }

    [Fact]
    public async Task TheStagedKeysAreNotPublic()
    {
        var ready = await ReadyAsync();
        using var _ = ready.User;

        Assert.All(ready.Pieces, piece =>
        {
            Assert.False(StorageKeys.IsPublic(piece.CutoutKey!));
            Assert.False(StorageKeys.IsPublic(piece.ThumbKey!));
        });
    }

    // ---- Validation ----

    [Theory]
    [InlineData("empty")]
    [InlineData("thirteen")]
    [InlineData("duplicate")]
    [InlineData("bad-primary")]
    [InlineData("bad-secondary")]
    [InlineData("clear-name")]
    [InlineData("set-and-clear")]
    public async Task AnInvalidBodyAnswers400ValidationAndWritesNothing(string kind)
    {
        var ready = await ReadyAsync();
        using var _ = ready.User;
        var id = ready.Pieces[0].Id;
        object body = kind switch
        {
            "empty" => new { pieces = Array.Empty<object>() },
            "thirteen" => new { pieces = Enumerable.Range(0, 13).Select(_ => new { id = Guid.NewGuid() }).ToArray() },
            "duplicate" => new { pieces = new[] { new { id }, new { id } } },
            "bad-primary" => new { pieces = new[] { new { id, primaryColor = "Rainbow" } } },
            "bad-secondary" => new { pieces = new[] { new { id, secondaryColor = "Rainbow" } } },
            "clear-name" => new { pieces = new[] { new { id, clear = ClearName } } },
            _ => new { pieces = new[] { new { id, secondaryColor = "White", clear = ClearSecondary } } },
        };

        using var response = await ready.User.Client.BulkConfirmRawAsync(ready.BatchId, body, Ct);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "Validation.Failed");
        Assert.Equal(BulkBatchStatus.Ready, (await factory.BulkBatchRowAsync(ready.BatchId, Ct)).Status);
        Assert.Empty(await ItemsAsync([id]));
    }

    [Fact]
    public async Task AnUnknownPieceAnswers400BulkUnknownPiece()
    {
        var ready = await ReadyAsync();
        using var _ = ready.User;

        using var response = await ready.User.Client.BulkConfirmRawAsync(ready.BatchId, [ready.Pieces[0].Id, Guid.NewGuid()], Ct);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "Wardrobe.BulkUnknownPiece");
        Assert.Empty(await ItemsAsync([ready.Pieces[0].Id]));
    }

    [Fact]
    public async Task APieceOfAnotherOwnBatchAnswers400BulkUnknownPiece()
    {
        var ready = await ReadyAsync();
        using var _ = ready.User;
        var other = await factory.RunBulkAsync(ready.User.Client, "bulk-3", Ct);
        var foreign = (await factory.BulkPieceRowsAsync(other.Id, Ct))[0].Id;

        using var response = await ready.User.Client.BulkConfirmRawAsync(ready.BatchId, [foreign], Ct);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "Wardrobe.BulkUnknownPiece");
        Assert.Empty(await ItemsAsync([foreign]));
    }

    [Fact]
    public async Task AnUnknownCategoryAnswers400UnknownCategory()
    {
        var ready = await ReadyAsync();
        using var _ = ready.User;

        using var response = await ready.User.Client.BulkConfirmRawAsync(
            ready.BatchId, new { pieces = new object[] { new { id = ready.Pieces[0].Id }, new { id = ready.Pieces[1].Id, categorySlug = "yok/boyle-bir-kategori" } } }, Ct);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "Wardrobe.UnknownCategory");
        Assert.Empty(await ItemsAsync([ready.Pieces[0].Id, ready.Pieces[1].Id]));
        Assert.Equal(BulkBatchStatus.Ready, (await factory.BulkBatchRowAsync(ready.BatchId, Ct)).Status);
    }

    // ---- States ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ABatchNotYetReadyAnswers409BulkNotReady(bool processed)
    {
        using var user = await factory.CreateUserAsync(Ct);
        user.Client.At(BulkAddClientExtensions.PinnedNow());
        var batch = processed ? await factory.ProcessBulkAsync(user.Client, "bulk-3", Ct) : await factory.StartBulkAsync(user.Client, "bulk-3", Ct);

        using var response = await user.Client.BulkConfirmRawAsync(batch.Id, [Guid.NewGuid()], Ct);

        await AssertProblemAsync(response, HttpStatusCode.Conflict, "Wardrobe.BulkNotReady");
    }

    [Fact]
    public async Task ACancelledBatchAnswers409BulkExpired()
    {
        var ready = await ReadyAsync();
        using var _ = ready.User;
        using (var cancel = await ready.User.Client.BulkCancelAsync(ready.BatchId, Ct))
        {
            Assert.Equal(HttpStatusCode.NoContent, cancel.StatusCode);
        }

        using var response = await ready.User.Client.BulkConfirmRawAsync(ready.BatchId, [ready.Pieces[0].Id], Ct);

        await AssertProblemAsync(response, HttpStatusCode.Conflict, "Wardrobe.BulkExpired");
    }

    [Fact]
    public async Task AnExpiredBatchAnswers409BulkExpired()
    {
        var ready = await ReadyAsync();
        using var _ = ready.User;
        await factory.CleanupBulkAsync(ready.User.Id, ready.Now.AddHours(25), Ct);
        Assert.Equal(BulkBatchStatus.Expired, (await factory.BulkBatchRowAsync(ready.BatchId, Ct)).Status);

        using var response = await ready.User.Client.BulkConfirmRawAsync(ready.BatchId, [ready.Pieces[0].Id], Ct);

        await AssertProblemAsync(response, HttpStatusCode.Conflict, "Wardrobe.BulkExpired");
    }

    [Fact]
    public async Task AReadyBatchPastItsExpiryAnswers409BulkExpired()
    {
        var ready = await ReadyAsync();
        using var _ = ready.User;
        ready.User.Client.At(ready.Now.AddHours(24).AddSeconds(1));

        using var response = await ready.User.Client.BulkConfirmRawAsync(ready.BatchId, [ready.Pieces[0].Id], Ct);

        await AssertProblemAsync(response, HttpStatusCode.Conflict, "Wardrobe.BulkExpired");
        Assert.Empty(await ItemsAsync([ready.Pieces[0].Id]));
    }

    [Fact]
    public async Task SomeoneElsesBatchAnswers404BulkNotFound()
    {
        var ready = await ReadyAsync();
        using var _ = ready.User;
        using var stranger = await factory.CreateUserAsync(Ct);
        stranger.Client.At(ready.Now);

        using var response = await stranger.Client.BulkConfirmRawAsync(ready.BatchId, [ready.Pieces[0].Id], Ct);

        await AssertProblemAsync(response, HttpStatusCode.NotFound, "Wardrobe.BulkNotFound");
        Assert.Empty(await ItemsAsync([ready.Pieces[0].Id]));
    }

    [Fact]
    public async Task AnUnknownBatchAnswers404BulkNotFound()
    {
        using var user = await factory.CreateUserAsync(Ct);

        using var response = await user.Client.BulkConfirmRawAsync(Guid.NewGuid(), [Guid.NewGuid()], Ct);

        await AssertProblemAsync(response, HttpStatusCode.NotFound, "Wardrobe.BulkNotFound");
    }

    // ---- Cancel ----

    [Fact]
    public async Task CancelAnswers204AndTheBatchIsCancelled()
    {
        var ready = await ReadyAsync();
        using var _ = ready.User;

        using var response = await ready.User.Client.BulkCancelAsync(ready.BatchId, Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var batch = await factory.BulkBatchRowAsync(ready.BatchId, Ct);
        Assert.Equal(BulkBatchStatus.Cancelled, batch.Status);
        Assert.Equal(ready.Now, batch.EndedAt);
    }

    [Fact]
    public async Task CancellingTwiceAnswers204Again()
    {
        var ready = await ReadyAsync();
        using var _ = ready.User;
        using var first = await ready.User.Client.BulkCancelAsync(ready.BatchId, Ct);

        using var second = await ready.User.Client.BulkCancelAsync(ready.BatchId, Ct);

        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
        Assert.Equal(BulkBatchStatus.Cancelled, (await factory.BulkBatchRowAsync(ready.BatchId, Ct)).Status);
    }

    [Fact]
    public async Task CancellingAnExpiredBatchAnswers204()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var now = BulkAddClientExtensions.PinnedNow();
        user.Client.At(now);
        var batchId = await factory.InsertBulkBatchAsync(user.Id, BulkBatchStatus.Expired, now.AddHours(-1), cancellationToken: Ct);

        using var response = await user.Client.BulkCancelAsync(batchId, Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task CancellingAConfirmedBatchAnswers409BulkAlreadyConfirmed()
    {
        var ready = await ReadyAsync();
        using var _ = ready.User;
        await ready.User.Client.BulkConfirmAsync(ready.BatchId, [ready.Pieces[0].Id], Ct);

        using var response = await ready.User.Client.BulkCancelAsync(ready.BatchId, Ct);

        await AssertProblemAsync(response, HttpStatusCode.Conflict, "Wardrobe.BulkAlreadyConfirmed");
        Assert.Equal(BulkBatchStatus.Confirmed, (await factory.BulkBatchRowAsync(ready.BatchId, Ct)).Status);
        Assert.Single(await ItemsAsync([ready.Pieces[0].Id]));
    }

    [Fact]
    public async Task CancelDeletesThePieces()
    {
        var ready = await ReadyAsync();
        using var _ = ready.User;

        using var response = await ready.User.Client.BulkCancelAsync(ready.BatchId, Ct);

        Assert.Empty(await factory.BulkPieceRowsAsync(ready.BatchId, Ct));
    }

    [Fact]
    public async Task CancelDeletesEveryObjectOfTheBatch()
    {
        var ready = await ReadyAsync();
        using var _ = ready.User;
        var prefix = StorageKeys.BulkBatchPrefix(ready.User.Id, ready.BatchId);
        Assert.NotEmpty(KeysUnder(ready.User.Id, prefix));

        using var response = await ready.User.Client.BulkCancelAsync(ready.BatchId, Ct);

        Assert.Empty(KeysUnder(ready.User.Id, prefix));
        Assert.NotNull((await factory.BulkBatchRowAsync(ready.BatchId, Ct)).ObjectsDeletedAt);
    }

    [Fact]
    public async Task CancellingAProcessingBatchDeletesItsPhoto()
    {
        using var user = await factory.CreateUserAsync(Ct);
        user.Client.At(BulkAddClientExtensions.PinnedNow());
        var batch = await factory.ProcessBulkAsync(user.Client, "bulk-3", Ct);

        using var response = await user.Client.BulkCancelAsync(batch.Id, Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Null(factory.Storage.Find(batch.OriginalKey));
        Assert.Equal(BulkBatchStatus.Cancelled, (await factory.BulkBatchRowAsync(batch.Id, Ct)).Status);
    }

    [Fact]
    public async Task CancellingSomeoneElsesBatchAnswers404()
    {
        var ready = await ReadyAsync();
        using var _ = ready.User;
        using var stranger = await factory.CreateUserAsync(Ct);

        using var response = await stranger.Client.BulkCancelAsync(ready.BatchId, Ct);

        await AssertProblemAsync(response, HttpStatusCode.NotFound, "Wardrobe.BulkNotFound");
        Assert.Equal(BulkBatchStatus.Ready, (await factory.BulkBatchRowAsync(ready.BatchId, Ct)).Status);
    }
}
