using Kombin.Api.Features.Catalog.Embeddings;
using Kombin.Api.Persistence.Entities.Catalog;

namespace Kombin.Api.Tests;

public class ProductEmbeddingRulesTests
{
    private const string Url = "https://img.test/a.jpg";

    private const string Model = "ViT-B-32/laion2b_s34b_b79k";

    private static readonly DateTimeOffset Now = new(2026, 11, 8, 3, 0, 0, TimeSpan.Zero);

    private static EmbeddingState Row(ProductEmbeddingStatus status, string url = Url, string model = Model, DateTimeOffset? next = null, DateTimeOffset? checkedAt = null) =>
        new(status, model, url, next, checkedAt ?? Now.AddHours(-1));

    [Fact]
    public void AProductWithoutARowIsDue() => Assert.True(ProductEmbeddingRules.IsDue(null, Url, Model, Now));

    [Fact]
    public void AReadyRowIsDueOnlyForANewImageOrANewModel()
    {
        Assert.False(ProductEmbeddingRules.IsDue(Row(ProductEmbeddingStatus.Ready), Url, Model, Now));
        Assert.True(ProductEmbeddingRules.IsDue(Row(ProductEmbeddingStatus.Ready), "https://img.test/b.jpg", Model, Now));
        Assert.True(ProductEmbeddingRules.IsDue(Row(ProductEmbeddingStatus.Ready, model: "ViT-B-32/openai"), Url, Model, Now));
    }

    [Fact]
    public void AnUnusableImageIsNotRetriedEveryRun()
    {
        Assert.False(ProductEmbeddingRules.IsDue(Row(ProductEmbeddingStatus.Unusable, checkedAt: Now.AddDays(-29)), Url, Model, Now));
        Assert.True(ProductEmbeddingRules.IsDue(Row(ProductEmbeddingStatus.Unusable, checkedAt: Now - ProductEmbeddingRules.UnusableRetryAfter), Url, Model, Now));
        Assert.True(ProductEmbeddingRules.IsDue(Row(ProductEmbeddingStatus.Unusable), "https://img.test/new.jpg", Model, Now));

        // A new vision model does not make a dead image worth fetching.
        Assert.False(ProductEmbeddingRules.IsDue(Row(ProductEmbeddingStatus.Unusable, model: "old"), Url, Model, Now));
    }

    [Fact]
    public void AFailedRowWaitsForItsBackOff()
    {
        Assert.False(ProductEmbeddingRules.IsDue(Row(ProductEmbeddingStatus.Failed, next: Now.AddMinutes(1)), Url, Model, Now));
        Assert.True(ProductEmbeddingRules.IsDue(Row(ProductEmbeddingStatus.Failed, next: Now), Url, Model, Now));
        Assert.True(ProductEmbeddingRules.IsDue(Row(ProductEmbeddingStatus.Failed, next: Now.AddHours(5)), "https://img.test/b.jpg", Model, Now));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(5, 16)]
    [InlineData(6, 24)]
    [InlineData(40, 24)]
    public void TheBackOffDoublesUpToADay(int attempts, int hours) =>
        Assert.Equal(Now.AddHours(hours), ProductEmbeddingRules.NextAttempt(attempts, Now));

    [Theory]
    [InlineData("https://img.test/primary.jpg", "https://img.test/first.jpg", "https://img.test/primary.jpg")]
    [InlineData(null, "https://img.test/first.jpg", "https://img.test/first.jpg")]
    [InlineData(" ", "https://img.test/first.jpg", "https://img.test/first.jpg")]
    [InlineData(null, null, null)]
    public void ThePrimaryImageIsUsedElseTheFirst(string? primary, string? first, string? expected) =>
        Assert.Equal(expected, ProductEmbeddingRules.SourceImage(primary, first));
}
