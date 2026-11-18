using Kombin.Api.Features.Wardrobe.BulkAdd;

namespace Kombin.Api.Tests;

/// <summary><c>BulkAdd:*</c> (task BA2): the defaults are the owner's answers to the design note, and a bad value refuses the start with every problem listed.</summary>
public sealed class BulkAddOptionsTests
{
    [Fact]
    public void TheDefaultsAreTheOwnersAnswersAndTheFlagIsOff()
    {
        var options = new BulkAddOptions();

        Assert.False(options.Enabled);
        Assert.Equal(12, options.MaxPiecesPerPhoto);
        Assert.Equal(8, options.MaxPhotosPerDay);
        Assert.Equal(3, options.MaxOpenBatches);
        Assert.Equal(TimeSpan.FromHours(24), options.StagingRetention);
        Assert.Equal(TimeSpan.FromDays(7), options.TombstoneRetention);
        Assert.Equal(0.92, options.DuplicateSimilarity);
        Assert.Equal(TimeSpan.FromSeconds(3), options.BusyRetryMin);
        Assert.Equal(TimeSpan.FromSeconds(10), options.BusyRetryMax);
        Assert.Equal(TimeSpan.FromSeconds(60), options.VisionTimeout);
        Assert.Equal(TimeSpan.FromMinutes(10), options.ReprocessAfter);
        Assert.Equal(TimeSpan.FromMinutes(15), options.InterruptedAfter);
        Assert.Equal(TimeSpan.FromHours(1), options.StagedUrlLifetime);
        Assert.Equal("BulkAdd", BulkAddOptions.SectionName);
    }

    [Fact]
    public void TheDefaultsAreValid()
    {
        Assert.Empty(BulkAddOptionsValidator.Problems(new BulkAddOptions()));
        Assert.True(new BulkAddOptionsValidator().Validate(null, new BulkAddOptions()).Succeeded);
    }

    [Fact]
    public void EveryProblemIsListedAtOnce()
    {
        var options = new BulkAddOptions
        {
            MaxPiecesPerPhoto = 13,
            MaxPhotosPerDay = 0,
            MaxOpenBatches = 0,
            StagingRetention = TimeSpan.Zero,
            TombstoneRetention = TimeSpan.FromHours(2),
            DuplicateSimilarity = 1.5,
            BusyRetryMin = TimeSpan.FromSeconds(-1),
            BusyRetryMax = TimeSpan.FromSeconds(-2),
            VisionTimeout = TimeSpan.FromMinutes(6),
            ReprocessAfter = TimeSpan.Zero,
            InterruptedAfter = TimeSpan.FromSeconds(30),
            StagedUrlLifetime = TimeSpan.Zero,
        };

        var problems = BulkAddOptionsValidator.Problems(options).ToList();

        Assert.Equal(12, problems.Count);
        Assert.All(problems, problem => Assert.StartsWith("BulkAdd:", problem, StringComparison.Ordinal));

        var result = new BulkAddOptionsValidator().Validate(null, options);
        Assert.True(result.Failed);
        Assert.Equal(problems, result.Failures);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(13)]
    [InlineData(-1)]
    public void ThePieceLimitStaysWithinWhatTheServiceSends(int pieces) =>
        Assert.Single(BulkAddOptionsValidator.Problems(new BulkAddOptions { MaxPiecesPerPhoto = pieces }));

    [Theory]
    [InlineData(1)]
    [InlineData(12)]
    public void AnyPieceLimitFromOneToTwelveIsFine(int pieces) =>
        Assert.Empty(BulkAddOptionsValidator.Problems(new BulkAddOptions { MaxPiecesPerPhoto = pieces }));

    [Fact]
    public void AMinimumBusyDelayAboveTheMaximumIsRefused() =>
        Assert.Single(BulkAddOptionsValidator.Problems(new BulkAddOptions { BusyRetryMin = TimeSpan.FromSeconds(11) }));

    [Fact]
    public void TheDuplicateThresholdCanBeOneButNotZero()
    {
        Assert.Empty(BulkAddOptionsValidator.Problems(new BulkAddOptions { DuplicateSimilarity = 1 }));
        Assert.Single(BulkAddOptionsValidator.Problems(new BulkAddOptions { DuplicateSimilarity = 0 }));
    }
}
