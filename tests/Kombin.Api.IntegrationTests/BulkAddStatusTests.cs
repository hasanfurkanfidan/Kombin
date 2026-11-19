using System.Net;
using System.Text.Json;
using Kombin.Api.Common.Storage;
using Kombin.Api.Features.Wardrobe.BulkAdd;
using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Wardrobe;
using Microsoft.EntityFrameworkCore;

namespace Kombin.Api.IntegrationTests;

/// <summary><c>GET /me/wardrobe/bulk/{id}</c> (task BA3): the whole review screen in one answer, in the server's Turkish.</summary>
public sealed class BulkAddStatusTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Noon = new(2026, 11, 18, 9, 0, 0, TimeSpan.Zero);

    private static readonly string[] FakeCandidates = ["ust-giyim/tisort", "ust-giyim/triko", "ust-giyim/gomlek"];

    private static JsonElement[] Pieces(JsonElement status) => [.. status.GetProperty("pieces").EnumerateArray()];

    [Fact]
    public async Task ReadyCarriesEverythingTheReviewScreenDraws()
    {
        using var user = await factory.CreateUserAsync(Ct);
        user.Client.At(Noon);
        var started = await factory.RunBulkAsync(user.Client, "bulk-3", Ct);
        var rows = await factory.BulkPieceRowsAsync(started.Id, Ct);

        var status = await user.Client.BulkStatusAsync(started.Id, Ct);

        Assert.Equal(started.Id, status.GetProperty("id").GetGuid());
        Assert.Equal("Ready", status.GetProperty("status").GetString());
        Assert.Equal(Noon.AddHours(24), status.GetProperty("expiresAt").GetDateTimeOffset());
        Assert.True(status.IsNull("refusal"));
        Assert.True(status.IsNull("failure"));
        Assert.True(status.IsNull("unseparated"));
        Assert.True(status.IsNull("cutOff"));
        Assert.True(status.IsNull("items"));

        var pieces = Pieces(status);
        Assert.Equal(rows.Select(row => row.Id), pieces.Select(piece => piece.GetProperty("id").GetGuid()));
        Assert.Equal([true, true, false], pieces.Select(piece => piece.GetProperty("preTicked").GetBoolean()));

        var first = pieces[0];
        Assert.Equal(Noon.AddHours(1), first.GetProperty("urlsExpireAt").GetDateTimeOffset());
        var box = first.GetProperty("box");
        Assert.Equal((0.05, 0.1, 0.28, 0.35), (box.GetProperty("x").GetDouble(), box.GetProperty("y").GetDouble(), box.GetProperty("w").GetDouble(), box.GetProperty("h").GetDouble()));
        Assert.Equal("Beige", first.GetProperty("primaryColor").GetString());
        Assert.True(first.IsNull("secondaryColor"));
        Assert.Equal("Solid", first.GetProperty("pattern").GetString());
        Assert.Empty(first.GetProperty("warnings").EnumerateArray());
        Assert.False(first.GetProperty("needsCategory").GetBoolean());
        Assert.Equal("ust-giyim/tisort", first.GetProperty("category").GetProperty("slug").GetString());
        Assert.False(string.IsNullOrEmpty(first.GetProperty("category").GetProperty("name").GetString()));
        Assert.True(first.IsNull("likelyDuplicate"));
        Assert.Empty(first.GetProperty("reasons").EnumerateArray());

        // The chips are the candidates the service named, as the catalog's own categories.
        var candidates = first.GetProperty("categoryCandidates").EnumerateArray().Select(candidate => candidate.GetProperty("slug").GetString()).ToList();
        Assert.Contains("ust-giyim/tisort", candidates);
        Assert.All(candidates, slug => Assert.Contains(slug, FakeCandidates));

        // The piece that is not pre-ticked says why, in the server's words.
        var reason = Assert.Single(pieces[2].GetProperty("reasons").EnumerateArray());
        Assert.Equal("cut_by_frame", reason.GetProperty("code").GetString());
        Assert.Equal(BulkAddCopy.Reasons["cut_by_frame"], reason.GetProperty("message").GetString());
    }

    [Fact]
    public async Task TheImagesArePrivatePresignedLinksOfTheStagedKeysNeverAPublicUrl()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var started = await factory.RunBulkAsync(user.Client, "bulk-3", Ct);
        var rows = await factory.BulkPieceRowsAsync(started.Id, Ct);

        var pieces = Pieces(await user.Client.BulkStatusAsync(started.Id, Ct));

        for (var index = 0; index < pieces.Length; index++)
        {
            var thumb = pieces[index].GetProperty("thumbUrl").GetString()!;
            var image = pieces[index].GetProperty("imageUrl").GetString()!;

            // The fake store signs a download as https://storage.test/kombin/{key}?X-Amz-Signature=...; a public URL would start with the CDN's base.
            Assert.StartsWith($"https://storage.test/kombin/{rows[index].ThumbKey}?X-Amz-Signature=", thumb, StringComparison.Ordinal);
            Assert.StartsWith($"https://storage.test/kombin/{rows[index].CutoutKey}?X-Amz-Signature=", image, StringComparison.Ordinal);
            Assert.DoesNotContain(FakeObjectStorage.PublicBaseUrl, thumb + image, StringComparison.Ordinal);
            Assert.False(StorageKeys.IsPublic(rows[index].ThumbKey!));
            Assert.False(StorageKeys.IsPublic(rows[index].CutoutKey!));
            Assert.NotEqual(thumb, image);
        }
    }

    [Fact]
    public async Task AskingAgainLaterRefreshesTheLinkExpiry()
    {
        using var user = await factory.CreateUserAsync(Ct);
        user.Client.At(Noon);
        var started = await factory.RunBulkAsync(user.Client, "bulk-3", Ct);

        user.Client.At(Noon.AddMinutes(50));
        var later = Pieces(await user.Client.BulkStatusAsync(started.Id, Ct));

        Assert.All(later, piece => Assert.Equal(Noon.AddMinutes(110), piece.GetProperty("urlsExpireAt").GetDateTimeOffset()));
    }

    [Fact]
    public async Task ABatchNotYetAnsweredHasNoPiecesAndNothingElse()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var created = await factory.StartBulkAsync(user.Client, "bulk-3", Ct);
        var processing = await factory.ProcessBulkAsync(user.Client, "bulk-3", Ct);

        foreach (var (batch, expected) in new[] { (created, "Created"), (processing, "Processing") })
        {
            var status = await user.Client.BulkStatusAsync(batch.Id, Ct);

            Assert.Equal(expected, status.GetProperty("status").GetString());
            Assert.Empty(status.GetProperty("pieces").EnumerateArray());
            Assert.True(status.IsNull("refusal"));
            Assert.True(status.IsNull("failure"));
            Assert.True(status.IsNull("unseparated"));
            Assert.True(status.IsNull("cutOff"));
        }
    }

    [Theory]
    [InlineData("closet")]
    [InlineData("rail")]
    [InlineData("pile")]
    [InlineData("worn")]
    [InlineData("nothing_found")]
    [InlineData("too_dark")]
    [InlineData("too_blurry")]
    public async Task ARefusalCarriesTheBriefsSentence(string code)
    {
        using var user = await factory.CreateUserAsync(Ct);
        var started = await factory.RunBulkAsync(user.Client, $"bulk-refusal-{code}", Ct);

        var status = await user.Client.BulkStatusAsync(started.Id, Ct);

        Assert.Equal("Refused", status.GetProperty("status").GetString());
        var refusal = status.GetProperty("refusal");
        Assert.Equal(code, refusal.GetProperty("code").GetString());
        Assert.Equal(BulkAddCopy.Refusals[code], refusal.GetProperty("message").GetString());
        Assert.Empty(status.GetProperty("pieces").EnumerateArray());
        Assert.True(status.IsNull("failure"));
    }

    [Fact]
    public async Task AnUnknownRefusalCodeStillGetsAnAnswerNeverAnError()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var started = await factory.RunBulkAsync(user.Client, "bulk-refusal-from_the_future", Ct);

        var refusal = (await user.Client.BulkStatusAsync(started.Id, Ct)).GetProperty("refusal");

        Assert.Equal("from_the_future", refusal.GetProperty("code").GetString());
        Assert.Equal(BulkAddCopy.UnknownRefusal, refusal.GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("bulk-busy-twice", "VisionBusy", true, "Şu an çok yoğunuz; birazdan tekrar dene.")]
    [InlineData("bulk-unreachable", "VisionUnavailable", true, "Şu an çok yoğunuz; birazdan tekrar dene.")]
    [InlineData("bulk-4xx", "PhotoUnreadable", false, "Bu fotoğrafı açamadık; başka bir fotoğrafla dene.")]
    [InlineData("bulk-bad-embedding", "AnalysisFailed", true, "Fotoğrafı işlerken bir şeyler ters gitti; tekrar dene.")]
    public async Task AFailureSaysWhetherTryingAgainMakesSense(string flavor, string code, bool retryable, string message)
    {
        using var user = await factory.CreateUserAsync(Ct);
        var started = await factory.RunBulkAsync(user.Client, flavor, Ct);

        var status = await user.Client.BulkStatusAsync(started.Id, Ct);

        Assert.Equal("Failed", status.GetProperty("status").GetString());
        var failure = status.GetProperty("failure");
        Assert.Equal((code, retryable, message), (failure.GetProperty("code").GetString(), failure.GetProperty("retryable").GetBoolean(), failure.GetProperty("message").GetString()));
        Assert.True(status.IsNull("refusal"));
        Assert.Empty(status.GetProperty("pieces").EnumerateArray());

        // The detail the operators read is never in the answer.
        Assert.DoesNotContain("vision_", status.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnseparatedAndCutOffAreServerOwnedSentencesAndIndependentOfEachOther()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var unseparated = await factory.RunBulkAsync(user.Client, "bulk-unseparated-2", Ct);
        var cutOff = await factory.RunBulkAsync(user.Client, "bulk-many-14", Ct);

        var first = await user.Client.BulkStatusAsync(unseparated.Id, Ct);
        var second = await user.Client.BulkStatusAsync(cutOff.Id, Ct);

        var block = first.GetProperty("unseparated");
        Assert.Equal(2, block.GetProperty("count").GetInt32());
        Assert.Equal("2 parçayı ayıramadık; onları tek tek ekleyebilirsin.", block.GetProperty("message").GetString());
        var region = Assert.Single(block.GetProperty("regions").EnumerateArray());
        Assert.Equal((0.1, 0.2, 0.4, 0.3), (region.GetProperty("x").GetDouble(), region.GetProperty("y").GetDouble(), region.GetProperty("w").GetDouble(), region.GetProperty("h").GetDouble()));
        Assert.True(first.IsNull("cutOff"));
        Assert.Equal(3, Pieces(first).Length);

        var cut = second.GetProperty("cutOff");
        Assert.Equal(2, cut.GetProperty("count").GetInt32());
        Assert.Equal(BulkAddCopy.CutOff(2), cut.GetProperty("message").GetString());
        Assert.Equal("Fotoğrafta 2 parça daha vardı; bir seferde bu kadarını gösterebiliyoruz. Kalanları ayrı bir fotoğrafla ekleyebilirsin.", cut.GetProperty("message").GetString());
        Assert.True(second.IsNull("unseparated"));
        Assert.Equal(12, Pieces(second).Length);
    }

    [Fact]
    public async Task ANewerVisionsUnknownReasonGetsTheGenericLineAndItsCode()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var batchId = await factory.WithDbAsync(async db =>
        {
            var batch = new BulkBatch { UserId = user.Id, Status = BulkBatchStatus.Ready, ExpiresAt = DateTimeOffset.UtcNow.AddHours(1), PiecesShown = 1 };
            db.BulkBatches.Add(batch);
            db.BulkPieces.Add(new BulkPiece
            {
                BatchId = batch.Id,
                UserId = user.Id,
                Reasons = ["touching", "a_reason_from_a_newer_vision"],
                CutoutKey = $"bulk/{user.Id:D}/{batch.Id:D}/p/staged.png",
                ThumbKey = $"bulk/{user.Id:D}/{batch.Id:D}/p/stagedthumb.webp",
            });
            await db.SaveChangesAsync(Ct);

            return batch.Id;
        });

        var piece = Pieces(await user.Client.BulkStatusAsync(batchId, Ct)).Single();

        var reasons = piece.GetProperty("reasons").EnumerateArray().ToList();
        Assert.Equal(["touching", "a_reason_from_a_newer_vision"], reasons.Select(reason => reason.GetProperty("code").GetString()));
        Assert.Equal([BulkAddCopy.Reasons["touching"], BulkAddCopy.UnknownReason], reasons.Select(reason => reason.GetProperty("message").GetString()));

        // A piece the service could not name a category for asks the user to pick one.
        Assert.True(piece.GetProperty("needsCategory").GetBoolean());
        Assert.True(piece.IsNull("category"));
        Assert.Empty(piece.GetProperty("categoryCandidates").EnumerateArray());
    }

    [Fact]
    public async Task AConfirmedBatchListsItsItemsInReadingOrder()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var first = await factory.InsertItemAsync(user.Id, "ust-giyim/tisort", ProductColor.White, cancellationToken: Ct);
        var second = await factory.InsertItemAsync(user.Id, "alt-giyim/jean", ProductColor.Blue, cancellationToken: Ct);
        var batchId = await factory.WithDbAsync(async db =>
        {
            var batch = new BulkBatch
            {
                UserId = user.Id,
                Status = BulkBatchStatus.Confirmed,
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
                ConfirmedItemIds = [second, first],
                ConfirmedAt = DateTimeOffset.UtcNow,
            };
            db.BulkBatches.Add(batch);
            await db.SaveChangesAsync(Ct);

            return batch.Id;
        });

        var status = await user.Client.BulkStatusAsync(batchId, Ct);

        Assert.Equal("Confirmed", status.GetProperty("status").GetString());
        Assert.Equal([second, first], status.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()));
        Assert.Empty(status.GetProperty("pieces").EnumerateArray());
    }

    [Theory]
    [InlineData("Cancelled")]
    [InlineData("Expired")]
    public async Task AnEndedBatchStillAnswersItsStatus(string state)
    {
        using var user = await factory.CreateUserAsync(Ct);
        var batchId = await factory.InsertBulkBatchAsync(user.Id, Enum.Parse<BulkBatchStatus>(state), DateTimeOffset.UtcNow.AddHours(1), cancellationToken: Ct);

        var status = await user.Client.BulkStatusAsync(batchId, Ct);

        Assert.Equal(state, status.GetProperty("status").GetString());
        Assert.Empty(status.GetProperty("pieces").EnumerateArray());
    }

    [Fact]
    public async Task AnotherUsersBatchIs404AndRevealsNothingOfItsExistenceOrItsPieces()
    {
        using var owner = await factory.CreateUserAsync(Ct);
        using var intruder = await factory.CreateUserAsync(Ct);
        var started = await factory.RunBulkAsync(owner.Client, "bulk-3", Ct);
        var pieces = await factory.BulkPieceRowsAsync(started.Id, Ct);

        using var foreign = await intruder.Client.BulkStatusRawAsync(started.Id, Ct);
        using var unknown = await intruder.Client.BulkStatusRawAsync(Guid.NewGuid(), Ct);

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        var foreignProblem = await foreign.ReadProblemAsync(Ct);
        var unknownProblem = await unknown.ReadProblemAsync(Ct);
        Assert.Equal("Wardrobe.BulkNotFound", foreignProblem.GetProperty("errorCode").GetString());
        Assert.Equal(unknownProblem.GetProperty("errorCode").GetString(), foreignProblem.GetProperty("errorCode").GetString());
        Assert.Equal(unknownProblem.GetProperty("title").GetString(), foreignProblem.GetProperty("title").GetString());
        Assert.Equal(unknownProblem.GetProperty("detail").GetString(), foreignProblem.GetProperty("detail").GetString());

        var body = foreignProblem.GetRawText();
        Assert.DoesNotContain(started.Id.ToString(), body, StringComparison.Ordinal);
        Assert.All(pieces, piece => Assert.DoesNotContain(piece.Id.ToString(), body, StringComparison.Ordinal));
        Assert.DoesNotContain(owner.Id.ToString(), body, StringComparison.Ordinal);

        // The owner still sees their own.
        Assert.Equal("Ready", (await owner.Client.BulkStatusAsync(started.Id, Ct)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task AnotherUsersStatusNeverLeaksIntoOnesOwnAnswer()
    {
        using var owner = await factory.CreateUserAsync(Ct);
        using var other = await factory.CreateUserAsync(Ct);
        var started = await factory.RunBulkAsync(owner.Client, "bulk-3", Ct);
        var mine = await factory.RunBulkAsync(other.Client, "bulk-refusal-rail", Ct);

        var status = await other.Client.BulkStatusAsync(mine.Id, Ct);

        Assert.Equal(mine.Id, status.GetProperty("id").GetGuid());
        Assert.DoesNotContain(started.Id.ToString(), status.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain(owner.Id.ToString(), status.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StagedPiecesAreOnlyEverNamedStagedAndStagedThumbInTheStatusAnswer()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var started = await factory.RunBulkAsync(user.Client, "bulk-3", Ct);

        var raw = (await user.Client.BulkStatusAsync(started.Id, Ct)).GetRawText();

        Assert.DoesNotContain("/cutout.", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("/thumb.", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("original", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("embedding", raw, StringComparison.OrdinalIgnoreCase);
    }
}
