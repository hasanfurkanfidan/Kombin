using System.Net;
using System.Text.Json.Nodes;
using Kombin.Api.Persistence.Entities.Wardrobe;

namespace Kombin.Api.IntegrationTests;

/// <summary>
/// "A bulk piece is the same record as a single one" (<c>docs/design/bulk-add.md</c>, What stays as decided): the same photo bytes through the single
/// add and through bulk add (fake flavour <c>bulk-single</c>, whose one piece is exactly the single add's analysis) give equal items.
/// </summary>
public sealed class BulkAddParityTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>One user adds the same photo twice: once alone, once as a bulk photo whose single piece is confirmed without edits.</summary>
    private async Task<(WardrobeItem Single, WardrobeItem Bulk, bool BulkNeedsCategory, bool SingleNeedsCategory)> BothAsync(string flavor)
    {
        using var user = await factory.CreateUserAsync(Ct);
        user.Client.At(BulkAddClientExtensions.PinnedNow());
        var photo = FakeVisionClient.Photo(flavor);

        var upload = await user.Client.CreateUploadUrlAsync(cancellationToken: Ct);
        var singleId = upload.GetProperty("itemId").GetGuid();
        factory.Storage.Upload(await factory.OriginalKeyAsync(singleId, Ct), photo, "image/jpeg");
        using (var process = await user.Client.ProcessAsync(singleId, Ct))
        {
            Assert.Equal(HttpStatusCode.Accepted, process.StatusCode);
        }

        await factory.AnalyzeAsync(singleId, Ct);

        var batchId = await factory.RunBulkWithPhotoAsync(user.Client, photo, Ct);
        var piece = Assert.Single(await factory.BulkPieceRowsAsync(batchId, Ct));
        var bulkNeedsCategory = (await user.Client.BulkStatusAsync(batchId, Ct)).GetProperty("pieces")[0].GetProperty("needsCategory").GetBoolean();
        await user.Client.BulkConfirmAsync(batchId, [piece.Id], Ct);

        var singleNeedsCategory = (await user.Client.GetItemAsync(singleId, Ct)).GetProperty("needsCategory").GetBoolean();
        var bulkItemNeedsCategory = (await user.Client.GetItemAsync(piece.Id, Ct)).GetProperty("needsCategory").GetBoolean();
        Assert.Equal(bulkNeedsCategory, bulkItemNeedsCategory);

        return (await factory.ItemRowAsync(singleId, Ct), await factory.ItemRowAsync(piece.Id, Ct), bulkNeedsCategory, singleNeedsCategory);
    }

    /// <summary>The stored analysis without the moment it was made.</summary>
    private static string WithoutTime(string? json)
    {
        var node = JsonNode.Parse(json!)!.AsObject();
        foreach (var key in node.Select(pair => pair.Key).Where(key => key.Equals("analyzedAt", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            node.Remove(key);
        }

        return node.ToJsonString();
    }

    [Fact]
    public async Task TheSamePhotoGivesTheSameAttributes()
    {
        var (single, bulk, _, _) = await BothAsync("bulk-single");

        Assert.NotNull(single.CategoryId);
        Assert.Equal(single.CategoryId, bulk.CategoryId);
        Assert.Equal(single.PrimaryColor, bulk.PrimaryColor);
        Assert.Equal(single.SecondaryColor, bulk.SecondaryColor);
        Assert.Equal(single.Pattern, bulk.Pattern);
        Assert.Equal(single.Formality, bulk.Formality);
        Assert.Equal(single.Seasons, bulk.Seasons);
        Assert.Equal(single.Warnings, bulk.Warnings);
        Assert.Equal(single.Status, bulk.Status);
    }

    [Fact]
    public async Task TheSamePhotoGivesTheSameEmbedding()
    {
        var (single, bulk, _, _) = await BothAsync("bulk-single");

        Assert.Equal(single.Embedding!.ToArray(), bulk.Embedding!.ToArray());
        Assert.Equal(single.EmbeddingModel, bulk.EmbeddingModel);
    }

    [Fact]
    public async Task TheSamePhotoGivesTheSameImages()
    {
        var (single, bulk, _, _) = await BothAsync("bulk-single");

        Assert.Equal(factory.Storage.Find(single.ImageCutoutKey!)!.Content, factory.Storage.Find(bulk.ImageCutoutKey!)!.Content);
        Assert.Equal(factory.Storage.Find(single.ImageThumbKey!)!.Content, factory.Storage.Find(bulk.ImageThumbKey!)!.Content);
        Assert.Equal(single.ImageCutoutKey!.Replace(single.Id.ToString("D"), bulk.Id.ToString("D"), StringComparison.Ordinal), bulk.ImageCutoutKey);
        Assert.Equal(single.ImageThumbKey!.Replace(single.Id.ToString("D"), bulk.Id.ToString("D"), StringComparison.Ordinal), bulk.ImageThumbKey);
    }

    [Fact]
    public async Task TheSamePhotoGivesTheSameStoredAnalysis()
    {
        var (single, bulk, _, _) = await BothAsync("bulk-single");

        Assert.Equal(WithoutTime(single.AnalysisJson), WithoutTime(bulk.AnalysisJson));
    }

    [Fact]
    public async Task AnUnsurePhotoNeedsACategoryOnBothPaths()
    {
        var (single, bulk, bulkNeedsCategory, singleNeedsCategory) = await BothAsync("bulk-single-unsure");

        Assert.True(singleNeedsCategory);
        Assert.True(bulkNeedsCategory);
        Assert.Null(single.CategoryId);
        Assert.Null(bulk.CategoryId);
    }

    [Fact]
    public async Task AnUnsurePhotoGivesTheSameStoredAnalysis()
    {
        var (single, bulk, _, _) = await BothAsync("bulk-single-unsure");

        Assert.Equal(WithoutTime(single.AnalysisJson), WithoutTime(bulk.AnalysisJson));
    }

    [Fact]
    public async Task ANotGarmentPhotoGivesTheSameWarnings()
    {
        var (single, bulk, _, _) = await BothAsync("bulk-single-not-garment");

        Assert.Contains(WardrobeItem.NotGarmentWarning, single.Warnings);
        Assert.Equal(single.Warnings, bulk.Warnings);
    }
}
