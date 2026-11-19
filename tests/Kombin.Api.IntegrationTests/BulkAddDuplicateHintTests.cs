using System.Net;
using Kombin.Api.Common.Storage;
using Kombin.Api.Common.Time;
using Kombin.Api.Common.Vision;
using Kombin.Api.Features.Wardrobe.BulkAdd;
using Kombin.Api.Features.Wardrobe.Jobs;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Wardrobe;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Kombin.Api.IntegrationTests;

/// <summary>"Dolabında benzeri var." (task BA3): a hint about the caller's own ready items, never a block and never someone else's.</summary>
public sealed class BulkAddDuplicateHintTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // A look shared by the photo and the item: tight members of a look are ~0.94 alike, a loose one ~0.83 to a tight one (FakeVisionClient.EmbeddingFor).
    private static string Look() => $"look-{Guid.NewGuid():N}";

    private static string PieceText(StartedBatch started, int index) => $"{started.PhotoText}#{index}";

    private async Task<(StartedBatch Started, Guid ItemId)> AnalyseWithItemAsync(
        TestUser user, string flavor, Func<StartedBatch, float[]> embedding, TestUser? owner = null, WardrobeItemStatus status = WardrobeItemStatus.Ready, bool archived = false, string? model = null)
    {
        var started = await factory.StartBulkAsync(user.Client, flavor, Ct);
        var itemId = await factory.InsertItemWithEmbeddingAsync((owner ?? user).Id, embedding(started), model, status, archived, Ct);
        using var process = await user.Client.BulkProcessAsync(started.Id, Ct);
        Assert.Equal(HttpStatusCode.Accepted, process.StatusCode);
        await factory.AnalyzeBulkAsync(started.Id, Ct);

        return (started, itemId);
    }

    [Fact]
    public async Task APieceThatLooksLikeOneOfTheUsersOwnItemsCarriesTheHintAndStaysPreTicked()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var (started, itemId) = await AnalyseWithItemAsync(user, "bulk-3", batch => FakeVisionClient.EmbeddingFor(PieceText(batch, 0)));

        var pieces = await factory.BulkPieceRowsAsync(started.Id, Ct);
        Assert.Equal(itemId, pieces[0].SimilarItemId);
        Assert.Null(pieces[1].SimilarItemId);
        Assert.Null(pieces[2].SimilarItemId);

        var status = await user.Client.BulkStatusAsync(started.Id, Ct);
        var answer = status.GetProperty("pieces").EnumerateArray().ToList();
        var hint = answer[0].GetProperty("likelyDuplicate");
        Assert.Equal(itemId, hint.GetProperty("itemId").GetGuid());
        Assert.Equal("Dolabında benzeri var.", hint.GetProperty("message").GetString());

        // The item's normal public thumbnail, never a number and never a staged link.
        var item = await factory.ItemRowAsync(itemId, Ct);
        Assert.Equal($"{FakeObjectStorage.PublicBaseUrl}/{item.ImageThumbKey}", hint.GetProperty("thumbUrl").GetString());
        Assert.DoesNotContain("similarity", hint.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.True(answer[1].IsNull("likelyDuplicate"));
        Assert.True(answer[2].IsNull("likelyDuplicate"));

        // A hint, never a block: the tick is what the service said, and nothing else about the batch changed.
        Assert.True(answer[0].GetProperty("preTicked").GetBoolean());
        Assert.Equal(3, (await factory.BulkBatchRowAsync(started.Id, Ct)).PiecesShown);
    }

    [Fact]
    public async Task AnotherUsersItemWithTheVeryVectorIsNeverAHint()
    {
        using var user = await factory.CreateUserAsync(Ct);
        using var stranger = await factory.CreateUserAsync(Ct);
        var (started, _) = await AnalyseWithItemAsync(user, "bulk-3", batch => FakeVisionClient.EmbeddingFor(PieceText(batch, 0)), owner: stranger);

        Assert.All(await factory.BulkPieceRowsAsync(started.Id, Ct), piece => Assert.Null(piece.SimilarItemId));
        var status = await user.Client.BulkStatusAsync(started.Id, Ct);
        Assert.All(status.GetProperty("pieces").EnumerateArray(), piece => Assert.True(piece.IsNull("likelyDuplicate")));
        Assert.DoesNotContain(stranger.Id.ToString(), status.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHintStoredForSomeoneElsesItemIsNotShownEvenIfItWereThere()
    {
        using var user = await factory.CreateUserAsync(Ct);
        using var stranger = await factory.CreateUserAsync(Ct);
        var started = await factory.RunBulkAsync(user.Client, "bulk-3", Ct);
        var strangerItem = await factory.InsertItemWithEmbeddingAsync(stranger.Id, FakeVisionClient.EmbeddingFor("anything"), cancellationToken: Ct);
        var pieceId = (await factory.BulkPieceRowsAsync(started.Id, Ct))[0].Id;
        await factory.WithDbAsync(db => db.BulkPieces.Where(piece => piece.Id == pieceId).ExecuteUpdateAsync(set => set.SetProperty(piece => piece.SimilarItemId, strangerItem), Ct));

        var status = await user.Client.BulkStatusAsync(started.Id, Ct);

        Assert.All(status.GetProperty("pieces").EnumerateArray(), piece => Assert.True(piece.IsNull("likelyDuplicate")));
        Assert.DoesNotContain(strangerItem.ToString(), status.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnrelatedItemIsNotAHint()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var (started, _) = await AnalyseWithItemAsync(user, "bulk-3", _ => FakeVisionClient.EmbeddingFor($"something-else-{Guid.NewGuid():N}"));

        Assert.All(await factory.BulkPieceRowsAsync(started.Id, Ct), piece => Assert.Null(piece.SimilarItemId));
    }

    [Fact]
    public async Task TheThresholdIsTheSettingsNotACodeConstant()
    {
        // A loose member of the look is ~0.83 to a tight one: below the default 0.92, above a threshold of 0.8.
        var look = Look();
        using var user = await factory.CreateUserAsync(Ct);
        var (started, itemId) = await AnalyseWithItemAsync(user, $"bulk-3:{look}", _ => FakeVisionClient.EmbeddingFor($"item {look} loose"));
        Assert.All(await factory.BulkPieceRowsAsync(started.Id, Ct), piece => Assert.Null(piece.SimilarItemId));

        // The same photo again with a lower threshold: the loose item is now a hint for the pieces of the look.
        var lax = await factory.StartBulkAsync(user.Client, $"bulk-3:{look}", Ct);
        using var process = await user.Client.BulkProcessAsync(lax.Id, Ct);
        Assert.Equal(HttpStatusCode.Accepted, process.StatusCode);
        await RunWithThresholdAsync(lax.Id, 0.8);

        var hinted = (await factory.BulkPieceRowsAsync(lax.Id, Ct)).Where(piece => piece.SimilarItemId == itemId).ToList();
        Assert.Equal(3, hinted.Count);
    }

    [Fact]
    public async Task ATightMemberOfTheSameLookIsAHintAtTheDefaultThreshold()
    {
        var look = Look();
        using var user = await factory.CreateUserAsync(Ct);
        var (started, itemId) = await AnalyseWithItemAsync(user, $"bulk-3:{look}", _ => FakeVisionClient.EmbeddingFor($"item {look}"));

        Assert.All(await factory.BulkPieceRowsAsync(started.Id, Ct), piece => Assert.Equal(itemId, piece.SimilarItemId));
    }

    [Theory]
    [InlineData("Processing", false, null)]
    [InlineData("Failed", false, null)]
    [InlineData("Ready", true, null)]
    [InlineData("Ready", false, "another-vision-model/v2")]
    public async Task OnlyReadyNotArchivedItemsOfTheSameModelAreCompared(string status, bool archived, string? model)
    {
        using var user = await factory.CreateUserAsync(Ct);
        var (started, _) = await AnalyseWithItemAsync(
            user, "bulk-3", batch => FakeVisionClient.EmbeddingFor(PieceText(batch, 0)), status: Enum.Parse<WardrobeItemStatus>(status), archived: archived, model: model);

        Assert.All(await factory.BulkPieceRowsAsync(started.Id, Ct), piece => Assert.Null(piece.SimilarItemId));
    }

    [Fact]
    public async Task TheNearestOfSeveralItemsWins()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var look = Look();
        var started = await factory.StartBulkAsync(user.Client, $"bulk-3:{look}", Ct);
        await factory.InsertItemWithEmbeddingAsync(user.Id, FakeVisionClient.EmbeddingFor($"far {look} loose"), cancellationToken: Ct);
        var nearest = await factory.InsertItemWithEmbeddingAsync(user.Id, FakeVisionClient.EmbeddingFor(PieceText(started, 1)), cancellationToken: Ct);
        using var process = await user.Client.BulkProcessAsync(started.Id, Ct);
        await factory.AnalyzeBulkAsync(started.Id, Ct);

        var pieces = await factory.BulkPieceRowsAsync(started.Id, Ct);

        Assert.Equal(nearest, pieces[1].SimilarItemId);
    }

    [Fact]
    public async Task TheHintIsCheckedAgainWhenReadAnItemArchivedOrDeletedSinceIsNotOne()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var (started, itemId) = await AnalyseWithItemAsync(user, "bulk-3", batch => FakeVisionClient.EmbeddingFor(PieceText(batch, 0)));
        Assert.False((await user.Client.BulkStatusAsync(started.Id, Ct)).GetProperty("pieces")[0].IsNull("likelyDuplicate"));

        await factory.WithDbAsync(db => db.WardrobeItems.Where(item => item.Id == itemId).ExecuteUpdateAsync(set => set.SetProperty(item => item.IsArchived, true), Ct));
        Assert.True((await user.Client.BulkStatusAsync(started.Id, Ct)).GetProperty("pieces")[0].IsNull("likelyDuplicate"));

        await factory.WithDbAsync(db => db.WardrobeItems.Where(item => item.Id == itemId).ExecuteUpdateAsync(set => set.SetProperty(item => item.IsArchived, false), Ct));
        Assert.False((await user.Client.BulkStatusAsync(started.Id, Ct)).GetProperty("pieces")[0].IsNull("likelyDuplicate"));

        await factory.WithDbAsync(db => db.WardrobeItems.Where(item => item.Id == itemId).ExecuteDeleteAsync(Ct));
        Assert.True((await user.Client.BulkStatusAsync(started.Id, Ct)).GetProperty("pieces")[0].IsNull("likelyDuplicate"));
    }

    private async Task RunWithThresholdAsync(Guid batchId, double threshold)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var settings = new BulkAddOptions
        {
            Enabled = true,
            DuplicateSimilarity = threshold,
            BusyRetryMin = TimeSpan.Zero,
            BusyRetryMax = TimeSpan.Zero,
        };
        var job = new BulkAddAnalyzeJob(
            services.GetRequiredService<KombinDbContext>(),
            services.GetRequiredService<IObjectStorage>(),
            services.GetRequiredService<IVisionClient>(),
            services.GetRequiredService<IClock>(),
            Options.Create(settings),
            NullLogger<BulkAddAnalyzeJob>.Instance);

        Assert.Equal(BulkAnalyzeOutcome.Ready, await job.AnalyzeAsync(batchId, Ct));
    }
}
