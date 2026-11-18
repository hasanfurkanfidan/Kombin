using System.Text.Json;
using Kombin.Api.Common.Storage;
using Kombin.Api.Common.Time;
using Kombin.Api.Common.Vision;
using Kombin.Api.Features.Wardrobe;
using Kombin.Api.Features.Wardrobe.BulkAdd;
using Kombin.Api.Features.Wardrobe.Jobs;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Wardrobe;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Kombin.Api.IntegrationTests;

/// <summary><c>bulk-add-analyze</c> (task BA3): the photo → vision → staged pieces, called directly like the other jobs.</summary>
public sealed class BulkAddAnalyzeJobTests(ApiFactory factory)
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string PrefixOf(Guid userId) => $"bulk/{userId:D}/";

    /// <summary>The job with its own settings, for the cases the shared host's defaults cannot show (a lower piece limit, the flag off).</summary>
    private async Task<BulkAnalyzeOutcome> RunWithAsync(Guid batchId, Action<BulkAddOptions> configure)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var settings = new BulkAddOptions { Enabled = true, BusyRetryMin = TimeSpan.Zero, BusyRetryMax = TimeSpan.Zero };
        configure(settings);
        var job = new BulkAddAnalyzeJob(
            services.GetRequiredService<KombinDbContext>(),
            services.GetRequiredService<IObjectStorage>(),
            services.GetRequiredService<IVisionClient>(),
            services.GetRequiredService<IClock>(),
            Options.Create(settings),
            NullLogger<BulkAddAnalyzeJob>.Instance);

        return await job.AnalyzeAsync(batchId, Ct);
    }

    [Fact]
    public async Task APhotoBecomesReadyWithItsPiecesStagedPrivatelyAndThePhotoIsDeleted()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var started = await factory.ProcessBulkAsync(user.Client, "bulk-3", Ct);
        Assert.NotNull(factory.Storage.Find(started.OriginalKey));

        Assert.Equal(BulkAnalyzeOutcome.Ready, await factory.AnalyzeBulkAsync(started.Id, Ct));

        var batch = await factory.BulkBatchRowAsync(started.Id, Ct);
        Assert.Equal(BulkBatchStatus.Ready, batch.Status);
        Assert.True(batch.Counted);
        Assert.Equal((3, 2, 0, 0), (batch.PiecesShown, batch.PiecesPreTicked, batch.CutOffCount, batch.UnseparatedCount));
        Assert.Equal(5300, batch.VisionMs);
        Assert.Equal(FakeVisionClient.Model, batch.EmbeddingModel);
        Assert.Null(batch.FailureCode);

        // The room photo is gone, from the row and from storage, the moment vision answered.
        Assert.Null(batch.OriginalKey);
        Assert.Null(factory.Storage.Find(started.OriginalKey));
        Assert.Contains(started.OriginalKey, factory.Storage.Deleted);

        var pieces = await factory.BulkPieceRowsAsync(started.Id, Ct);
        Assert.Equal([0, 1, 2], pieces.Select(piece => piece.SortOrder));
        Assert.Equal([true, true, false], pieces.Select(piece => piece.PreTicked));
        Assert.Equal<string[]>([[], [], ["cut_by_frame"]], pieces.Select(piece => piece.Reasons));
        Assert.All(pieces, piece =>
        {
            Assert.Equal((started.Id, user.Id), (piece.BatchId, piece.UserId));
            Assert.Equal(WardrobeItem.EmbeddingDimensions, piece.Embedding!.ToArray().Length);
            Assert.Equal(FakeVisionClient.Model, piece.EmbeddingModel);
            Assert.Null(piece.SimilarItemId);
            Assert.NotNull(piece.AnalysisJson);
        });

        // The box of piece 1 is what the fake sent.
        Assert.Equal((0.35, 0.1, 0.28, 0.35), (pieces[1].BoxX, pieces[1].BoxY, pieces[1].BoxW, pieces[1].BoxH));
    }

    [Fact]
    public async Task EachPieceIsStagedUnderAPrivateNameAsVisionSentIt()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var started = await factory.RunBulkAsync(user.Client, "bulk-3", Ct);

        var pieces = await factory.BulkPieceRowsAsync(started.Id, Ct);

        for (var index = 0; index < pieces.Count; index++)
        {
            var piece = pieces[index];
            Assert.Equal($"bulk/{user.Id:D}/{started.Id:D}/{piece.Id:D}/staged.png", piece.CutoutKey);
            Assert.Equal($"bulk/{user.Id:D}/{started.Id:D}/{piece.Id:D}/stagedthumb.webp", piece.ThumbKey);

            // The review image is the cut-out unchanged, the grid image vision's thumbnail unchanged.
            var cutout = factory.Storage.Find(piece.CutoutKey!);
            var thumb = factory.Storage.Find(piece.ThumbKey!);
            Assert.Equal(FakeVisionClient.BulkCutout(started.PhotoText, index), cutout?.Content);
            Assert.Equal("image/png", cutout?.ContentType);
            Assert.Equal(FakeVisionClient.BulkThumbnail(started.PhotoText, index), thumb?.Content);
            Assert.Equal("image/webp", thumb?.ContentType);
        }

        // Nothing staged is ever stored under a public name: not cutout.* or thumb.*, so the bucket policy never exposes it and no cache keeps it.
        var keys = factory.Storage.KeysOfUser(user.Id);
        Assert.Equal(6, keys.Count);
        Assert.All(keys, key =>
        {
            Assert.StartsWith(PrefixOf(user.Id), key, StringComparison.Ordinal);
            Assert.False(StorageKeys.IsPublic(key), key);
            Assert.DoesNotContain("/cutout.", key, StringComparison.Ordinal);
            Assert.DoesNotContain("/thumb.", key, StringComparison.Ordinal);
            Assert.Equal(StorageKeys.PrivateCacheControl, factory.Storage.CacheControlOf(key));
        });
    }

    [Fact]
    public async Task APiecesAttributesAreWhatTheSingleAddsAnalysisMakesOfTheSameAnswer()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var started = await factory.RunBulkAsync(user.Client, "bulk-3", Ct);
        var vision = factory.Services.GetRequiredService<IVisionClient>();
        var answer = (await vision.BulkAsync(System.Text.Encoding.UTF8.GetBytes(started.PhotoText), "image/jpeg", TimeSpan.FromSeconds(1), Ct)).Value!;
        var categories = await factory.WithDbAsync(db => db.Categories.AsNoTracking().ToDictionaryAsync(category => category.Slug, category => category.Id, Ct));

        var pieces = await factory.BulkPieceRowsAsync(started.Id, Ct);

        Assert.Equal(answer.Pieces.Count, pieces.Count);
        for (var index = 0; index < pieces.Count; index++)
        {
            var expected = WardrobeAnalysis.Map(answer.Pieces[index].Analysis);
            var piece = pieces[index];
            var expectedCategory = expected.CategorySlug is { } slug && categories.TryGetValue(slug, out var id) ? id : (Guid?)null;

            Assert.Equal(expectedCategory, piece.CategoryId);
            Assert.Equal((expected.PrimaryColor, expected.SecondaryColor, expected.Pattern), (piece.PrimaryColor, piece.SecondaryColor, piece.Pattern));
            Assert.Equal(expected.Warnings, piece.Warnings);
            if (expectedCategory is not null)
            {
                Assert.Equal((expected.Formality, expected.Seasons), (piece.Formality, piece.Seasons));
            }

            var stored = WardrobeAnalysis.Read(piece.AnalysisJson)!;
            Assert.Equal(answer.Pieces[index].Analysis.CategoryCandidates, stored.CategoryCandidates);
            Assert.Equal(FakeVisionClient.Model, stored.EmbeddingModel);
        }

        // The sanity the comparison stands on: the first piece is a t-shirt the catalog knows, beige.
        Assert.NotNull(pieces[0].CategoryId);
        Assert.Equal(ProductColor.Beige, pieces[0].PrimaryColor);
    }

    [Theory]
    [InlineData("closet")]
    [InlineData("rail")]
    [InlineData("pile")]
    [InlineData("worn")]
    [InlineData("nothing_found")]
    [InlineData("too_dark")]
    [InlineData("too_blurry")]
    [InlineData("a_code_from_a_newer_vision")]
    public async Task ARefusalIsKeptAsSentCountsTowardTheDayAndDeletesThePhoto(string code)
    {
        using var user = await factory.CreateUserAsync(Ct);
        var started = await factory.ProcessBulkAsync(user.Client, $"bulk-refusal-{code}", Ct);

        Assert.Equal(BulkAnalyzeOutcome.Refused, await factory.AnalyzeBulkAsync(started.Id, Ct));

        var batch = await factory.BulkBatchRowAsync(started.Id, Ct);
        Assert.Equal((BulkBatchStatus.Refused, code, true), (batch.Status, batch.Refusal, batch.Counted));
        Assert.Equal(5300, batch.VisionMs);
        Assert.Null(batch.OriginalKey);
        Assert.Null(factory.Storage.Find(started.OriginalKey));
        Assert.Empty(await factory.BulkPieceRowsAsync(started.Id, Ct));
        Assert.Empty(factory.Storage.KeysOfUser(user.Id));
    }

    [Fact]
    public async Task BusyOnceIsAskedAgainAndThenReady()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var started = await factory.ProcessBulkAsync(user.Client, "bulk-busy", Ct);

        Assert.Equal(BulkAnalyzeOutcome.Ready, await factory.AnalyzeBulkAsync(started.Id, Ct));

        Assert.Equal(2, FakeVisionClient.BulkCalls(started.PhotoText));
        var batch = await factory.BulkBatchRowAsync(started.Id, Ct);
        Assert.Equal((BulkBatchStatus.Ready, 2, true), (batch.Status, batch.AnalysisAttempts, batch.Counted));
        Assert.Equal(3, (await factory.BulkPieceRowsAsync(started.Id, Ct)).Count);
        Assert.Null(batch.OriginalKey);
    }

    [Fact]
    public async Task BusyTwiceFailsWithoutAThirdTryKeepsThePhotoAndIsNotCounted()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var started = await factory.ProcessBulkAsync(user.Client, "bulk-busy-twice", Ct);

        Assert.Equal(BulkAnalyzeOutcome.Failed, await factory.AnalyzeBulkAsync(started.Id, Ct));

        Assert.Equal(2, FakeVisionClient.BulkCalls(started.PhotoText));
        var batch = await factory.BulkBatchRowAsync(started.Id, Ct);
        Assert.Equal((BulkBatchStatus.Failed, BulkFailureCode.VisionBusy, false), (batch.Status, batch.FailureCode, batch.Counted));
        Assert.Contains("busy", batch.FailureDetail, StringComparison.Ordinal);
        Assert.Equal(started.OriginalKey, batch.OriginalKey);
        Assert.NotNull(factory.Storage.Find(started.OriginalKey));
        Assert.Empty(await factory.BulkPieceRowsAsync(started.Id, Ct));
    }

    [Fact]
    public async Task AnUnreachableServiceIsAskedOnceMoreThenVisionUnavailable()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var started = await factory.ProcessBulkAsync(user.Client, "bulk-unreachable", Ct);

        Assert.Equal(BulkAnalyzeOutcome.Failed, await factory.AnalyzeBulkAsync(started.Id, Ct));

        Assert.Equal(2, FakeVisionClient.BulkCalls(started.PhotoText));
        var batch = await factory.BulkBatchRowAsync(started.Id, Ct);
        Assert.Equal((BulkBatchStatus.Failed, BulkFailureCode.VisionUnavailable), (batch.Status, batch.FailureCode));
        Assert.Equal(started.OriginalKey, batch.OriginalKey);
    }

    [Fact]
    public async Task APermanent4xxIsNotRetriedAndFailsAsPhotoUnreadable()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var started = await factory.ProcessBulkAsync(user.Client, "bulk-4xx", Ct);

        Assert.Equal(BulkAnalyzeOutcome.Failed, await factory.AnalyzeBulkAsync(started.Id, Ct));

        Assert.Equal(1, FakeVisionClient.BulkCalls(started.PhotoText));
        var batch = await factory.BulkBatchRowAsync(started.Id, Ct);
        Assert.Equal((BulkBatchStatus.Failed, BulkFailureCode.PhotoUnreadable, false), (batch.Status, batch.FailureCode, batch.Counted));
        Assert.Equal(started.OriginalKey, batch.OriginalKey);
    }

    [Theory]
    [InlineData("bulk-bad-embedding", "embedding has 3 dimensions")]
    [InlineData("bulk-bad-image", "not base64")]
    public async Task ABadAnswerFailsTheWholeBatchBeforeAnythingIsWritten(string flavor, string detail)
    {
        using var user = await factory.CreateUserAsync(Ct);
        var started = await factory.ProcessBulkAsync(user.Client, flavor, Ct);

        Assert.Equal(BulkAnalyzeOutcome.Failed, await factory.AnalyzeBulkAsync(started.Id, Ct));

        var batch = await factory.BulkBatchRowAsync(started.Id, Ct);
        Assert.Equal((BulkBatchStatus.Failed, BulkFailureCode.AnalysisFailed, false), (batch.Status, batch.FailureCode, batch.Counted));
        Assert.Contains(detail, batch.FailureDetail, StringComparison.Ordinal);
        Assert.Equal(started.OriginalKey, batch.OriginalKey);
        Assert.Empty(await factory.BulkPieceRowsAsync(started.Id, Ct));

        // Only the photo is in storage: no piece of a bad answer was staged.
        Assert.Equal([started.OriginalKey], factory.Storage.KeysOfUser(user.Id));
    }

    [Fact]
    public async Task AStorageFailureWhileStagingFailsTheBatchAndLeavesNoStagedObject()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var started = await factory.ProcessBulkAsync(user.Client, "bulk-3", Ct);
        var name = $"bulk-staging-{started.Id:N}";
        factory.Storage.FailPuts(name, key => key.Contains(started.Id.ToString("D"), StringComparison.Ordinal) && key.EndsWith("stagedthumb.webp", StringComparison.Ordinal));

        try
        {
            Assert.Equal(BulkAnalyzeOutcome.Failed, await factory.AnalyzeBulkAsync(started.Id, Ct));
        }
        finally
        {
            factory.Storage.StopFailingPuts(name);
        }

        var batch = await factory.BulkBatchRowAsync(started.Id, Ct);
        Assert.Equal((BulkBatchStatus.Failed, BulkFailureCode.AnalysisFailed, false), (batch.Status, batch.FailureCode, batch.Counted));
        Assert.Equal(started.OriginalKey, batch.OriginalKey);
        Assert.Empty(await factory.BulkPieceRowsAsync(started.Id, Ct));
        Assert.Equal([started.OriginalKey], factory.Storage.KeysOfUser(user.Id));
    }

    [Fact]
    public async Task AMissingPhotoFailsAsPhotoUnreadable()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var started = await factory.ProcessBulkAsync(user.Client, "bulk-3", Ct);
        await factory.Storage.DeleteAsync([started.OriginalKey], Ct);

        Assert.Equal(BulkAnalyzeOutcome.Failed, await factory.AnalyzeBulkAsync(started.Id, Ct));

        var batch = await factory.BulkBatchRowAsync(started.Id, Ct);
        Assert.Equal((BulkBatchStatus.Failed, BulkFailureCode.PhotoUnreadable), (batch.Status, batch.FailureCode));
        Assert.Equal(0, FakeVisionClient.BulkCalls(started.PhotoText));
    }

    [Fact]
    public async Task APhotoThatFailedToDeleteKeepsItsKeyForTheBatchsPrefixSweep()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var started = await factory.ProcessBulkAsync(user.Client, "bulk-3", Ct);
        factory.Storage.FailDeletesUnder(started.OriginalKey);

        try
        {
            Assert.Equal(BulkAnalyzeOutcome.Ready, await factory.AnalyzeBulkAsync(started.Id, Ct));
        }
        finally
        {
            factory.Storage.StopFailingDeletes(started.OriginalKey);
        }

        var batch = await factory.BulkBatchRowAsync(started.Id, Ct);
        Assert.Equal(BulkBatchStatus.Ready, batch.Status);
        Assert.Equal(started.OriginalKey, batch.OriginalKey);
        Assert.Equal(3, (await factory.BulkPieceRowsAsync(started.Id, Ct)).Count);
    }

    [Fact]
    public async Task APhotoWithMorePiecesThanTheBatchKeepsIsCutOffInReadingOrderAndCounted()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var started = await factory.ProcessBulkAsync(user.Client, "bulk-many-6", Ct);

        Assert.Equal(BulkAnalyzeOutcome.Ready, await RunWithAsync(started.Id, settings => settings.MaxPiecesPerPhoto = 4));

        var batch = await factory.BulkBatchRowAsync(started.Id, Ct);
        Assert.Equal((4, 2, 4), (batch.PiecesShown, batch.CutOffCount, batch.PiecesPreTicked));
        var pieces = await factory.BulkPieceRowsAsync(started.Id, Ct);
        Assert.Equal([0, 1, 2, 3], pieces.Select(piece => piece.SortOrder));

        // The first four of the service's reading order stayed: their images are the first four the fake made.
        for (var index = 0; index < 4; index++)
        {
            Assert.Equal(FakeVisionClient.BulkCutout(started.PhotoText, index), factory.Storage.Find(pieces[index].CutoutKey!)?.Content);
        }

        // Nothing of the two left out was staged: four pieces' two images and nothing else (the photo is deleted).
        Assert.Equal(8, factory.Storage.KeysOfUser(user.Id).Count);
    }

    [Fact]
    public async Task TwelvePiecesIsTheDefaultLimitAndMoreAreCutOff()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var started = await factory.RunBulkAsync(user.Client, "bulk-many-15", Ct);

        var batch = await factory.BulkBatchRowAsync(started.Id, Ct);

        Assert.Equal((12, 3), (batch.PiecesShown, batch.CutOffCount));
        Assert.Equal(12, (await factory.BulkPieceRowsAsync(started.Id, Ct)).Count);
    }

    [Fact]
    public async Task UnseparatedGarmentsAreCountedWithTheirRegionsAndNoPiecesIsAValidReadyAnswer()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var started = await factory.RunBulkAsync(user.Client, "bulk-many-0:bulk-unseparated-2", Ct);

        var batch = await factory.BulkBatchRowAsync(started.Id, Ct);

        Assert.Equal((BulkBatchStatus.Ready, 2, 0, true), (batch.Status, batch.UnseparatedCount, batch.PiecesShown, batch.Counted));
        var regions = JsonSerializer.Deserialize<List<VisionRegionBox>>(batch.UnseparatedRegions!, Web)!;
        Assert.Equal([new VisionRegionBox(0.1, 0.2, 0.4, 0.3)], regions);
        Assert.Empty(await factory.BulkPieceRowsAsync(started.Id, Ct));
        Assert.Null(batch.OriginalKey);
    }

    [Fact]
    public async Task ASecondRunExitsAndVisionIsAskedOnce()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var started = await factory.ProcessBulkAsync(user.Client, "bulk-3", Ct);

        Assert.Equal(BulkAnalyzeOutcome.Ready, await factory.AnalyzeBulkAsync(started.Id, Ct));
        Assert.Equal(BulkAnalyzeOutcome.Skipped, await factory.AnalyzeBulkAsync(started.Id, Ct));

        Assert.Equal(1, FakeVisionClient.BulkCalls(started.PhotoText));
        Assert.Equal(3, (await factory.BulkPieceRowsAsync(started.Id, Ct)).Count);
    }

    [Fact]
    public async Task TwoRunsAtOnceAnalyseTheBatchOnce()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var started = await factory.ProcessBulkAsync(user.Client, "bulk-3", Ct);

        var outcomes = await Task.WhenAll(factory.AnalyzeBulkAsync(started.Id, Ct), factory.AnalyzeBulkAsync(started.Id, Ct));

        Assert.Equal(1, outcomes.Count(outcome => outcome == BulkAnalyzeOutcome.Ready));
        Assert.Equal(1, outcomes.Count(outcome => outcome == BulkAnalyzeOutcome.Skipped));
        Assert.Equal(1, FakeVisionClient.BulkCalls(started.PhotoText));
        Assert.Equal(3, (await factory.BulkPieceRowsAsync(started.Id, Ct)).Count);
    }

    [Theory]
    [InlineData("Created")]
    [InlineData("Ready")]
    [InlineData("Failed")]
    [InlineData("Cancelled")]
    [InlineData("Expired")]
    [InlineData("Confirmed")]
    public async Task OnlyAProcessingBatchIsAnalysed(string status)
    {
        using var user = await factory.CreateUserAsync(Ct);
        var started = await factory.StartBulkAsync(user.Client, "bulk-3", Ct);
        await factory.WithDbAsync(db => db.BulkBatches.Where(batch => batch.Id == started.Id).ExecuteUpdateAsync(set => set.SetProperty(batch => batch.Status, Enum.Parse<BulkBatchStatus>(status)), Ct));

        Assert.Equal(BulkAnalyzeOutcome.Skipped, await factory.AnalyzeBulkAsync(started.Id, Ct));

        Assert.Equal(0, FakeVisionClient.BulkCalls(started.PhotoText));
        Assert.Empty(await factory.BulkPieceRowsAsync(started.Id, Ct));
    }

    [Fact]
    public async Task AnUnknownBatchIsSkipped() =>
        Assert.Equal(BulkAnalyzeOutcome.Skipped, await factory.AnalyzeBulkAsync(Guid.NewGuid(), Ct));

    [Fact]
    public async Task WithTheFlagOffTheJobDoesNothing()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var started = await factory.ProcessBulkAsync(user.Client, "bulk-3", Ct);

        Assert.Equal(BulkAnalyzeOutcome.Skipped, await RunWithAsync(started.Id, settings => settings.Enabled = false));

        Assert.Equal(0, FakeVisionClient.BulkCalls(started.PhotoText));
        Assert.Equal(BulkBatchStatus.Processing, (await factory.BulkBatchRowAsync(started.Id, Ct)).Status);
    }

    [Fact]
    public async Task ABatchCancelledWhileVisionWorkedIsLeftAloneAndNothingStaysStaged()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var started = await factory.ProcessBulkAsync(user.Client, "bulk-3", Ct);

        // The batch ends between the claim and the commit: the fake cancels it while it answers.
        FakeVisionClient.OnBulk(started.PhotoText, () => factory.WithDbAsync(db => db.BulkBatches.Where(batch => batch.Id == started.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(batch => batch.Status, BulkBatchStatus.Cancelled), Ct)).GetAwaiter().GetResult());
        try
        {
            Assert.Equal(BulkAnalyzeOutcome.Skipped, await factory.AnalyzeBulkAsync(started.Id, Ct));
        }
        finally
        {
            FakeVisionClient.OnBulk(started.PhotoText, null);
        }

        Assert.Equal(BulkBatchStatus.Cancelled, (await factory.BulkBatchRowAsync(started.Id, Ct)).Status);
        Assert.Empty(await factory.BulkPieceRowsAsync(started.Id, Ct));
        Assert.Equal([started.OriginalKey], factory.Storage.KeysOfUser(user.Id));
    }
}
