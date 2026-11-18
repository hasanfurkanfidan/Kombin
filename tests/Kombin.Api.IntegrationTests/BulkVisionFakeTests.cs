using Kombin.Api.Common.Vision;

namespace Kombin.Api.IntegrationTests;

/// <summary>The fake's bulk flavours (task BA2): later tests rely on exactly these answers, so they are pinned here.</summary>
public sealed class BulkVisionFakeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private static Task<VisionCall<VisionBulk>> AskAsync(byte[] photo) => new FakeVisionClient().BulkAsync(photo, "image/jpeg", Timeout, Ct);

    [Fact]
    public async Task ThreePiecesTwoPreTickedAndOneWithAReason()
    {
        var photo = FakeVisionClient.Photo("bulk-3");
        var text = System.Text.Encoding.UTF8.GetString(photo);

        var bulk = (await AskAsync(photo)).Value!;

        Assert.Null(bulk.Refusal);
        Assert.Equal([true, true, false], bulk.Pieces.Select(piece => piece.PreTicked));
        Assert.Equal([[], [], ["cut_by_frame"]], bulk.Pieces.Select(piece => piece.Reasons.ToArray()));
        Assert.All(bulk.Pieces, piece => Assert.Equal(piece.PreTicked, piece.Reasons.Count == 0));
        Assert.Null(bulk.Unseparated);
        Assert.Equal(5300, bulk.StagesMs!["total"]);

        // Each piece is its own: cut-out, thumbnail and embedding differ, and the cut-out is the one the test can compute.
        Assert.Equal(3, bulk.Pieces.Select(piece => piece.Analysis.Cutout).Distinct().Count());
        Assert.Equal(3, bulk.Pieces.Select(piece => piece.Analysis.Thumbnail).Distinct().Count());
        Assert.Equal(Convert.ToBase64String(FakeVisionClient.BulkCutout(text, 1)), bulk.Pieces[1].Analysis.Cutout);
        Assert.All(bulk.Pieces, piece => Assert.Equal(512, piece.Analysis.Embedding.Length));
        Assert.NotEqual(bulk.Pieces[0].Analysis.Embedding, bulk.Pieces[1].Analysis.Embedding);
        Assert.Equal(1, FakeVisionClient.BulkCalls(text));
    }

    [Theory]
    [InlineData("closet")]
    [InlineData("too_blurry")]
    [InlineData("nothing_found")]
    [InlineData("a_new_refusal")]
    public async Task ARefusalHasNoPieces(string code)
    {
        var bulk = (await AskAsync(FakeVisionClient.Photo($"bulk-refusal-{code}"))).Value!;

        Assert.Equal(code, bulk.Refusal);
        Assert.Empty(bulk.Pieces);
    }

    [Fact]
    public async Task BusyOnceThenTheAnswerAndBusyTwiceNeverTheAnswer()
    {
        var once = FakeVisionClient.Photo("bulk-busy");

        var first = await AskAsync(once);
        Assert.True(first.IsTransient);
        Assert.Equal((503, "busy", TimeSpan.FromSeconds(5)), (first.Status, first.Detail, first.RetryAfter));
        Assert.Equal(3, (await AskAsync(once)).Value!.Pieces.Count);

        var twice = FakeVisionClient.Photo("bulk-busy-twice");
        for (var call = 0; call < 3; call++)
        {
            var busy = await AskAsync(twice);
            Assert.Null(busy.Value);
            Assert.Equal(503, busy.Status);
        }
    }

    [Fact]
    public async Task APermanentRefusalOfTheFileAndAnUnreachableServiceAreDifferentFailures()
    {
        var unreadable = await AskAsync(FakeVisionClient.Photo("bulk-4xx"));
        Assert.False(unreadable.IsTransient);
        Assert.Equal(400, unreadable.Status);

        var down = await AskAsync(FakeVisionClient.Photo("bulk-unreachable"));
        Assert.True(down.IsTransient);
        Assert.Null(down.Status);
    }

    [Fact]
    public async Task ABadAnswerIsAnAnswerWithABrokenPiece()
    {
        var embedding = (await AskAsync(FakeVisionClient.Photo("bulk-bad-embedding"))).Value!;
        Assert.Equal([512, 3, 512], embedding.Pieces.Select(piece => piece.Analysis.Embedding.Length));

        var image = (await AskAsync(FakeVisionClient.Photo("bulk-bad-image"))).Value!;
        Assert.Throws<FormatException>(() => Convert.FromBase64String(image.Pieces[0].Analysis.Cutout));
    }

    [Fact]
    public async Task ManyPiecesAndUnseparatedGarmentsComeWithTheirNumbers()
    {
        var many = (await AskAsync(FakeVisionClient.Photo("bulk-many-14"))).Value!;
        Assert.Equal(14, many.Pieces.Count);
        Assert.All(many.Pieces, piece => Assert.True(piece.PreTicked));

        var unseparated = (await AskAsync(FakeVisionClient.Photo("bulk-unseparated-2"))).Value!;
        Assert.Equal(3, unseparated.Pieces.Count);
        Assert.Equal(2, unseparated.Unseparated!.Count);
        Assert.Single(unseparated.Unseparated.Regions);
    }
}
