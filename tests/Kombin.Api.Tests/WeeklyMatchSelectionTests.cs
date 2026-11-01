using Kombin.Api.Features.Notification;
using Kombin.Api.Features.Profile;
using Kombin.Api.Persistence.Entities.Catalog;

namespace Kombin.Api.Tests;

public class WeeklyMatchSelectionTests
{
    private static readonly ProductTraits AnyTraits = new(ProductColor.Black, 3, ProductFit.Regular, Season.AllYear, ProductPattern.Solid, "ust-giyim/gomlek");

    private static WeeklyCandidate Candidate(string name, Audience audience = Audience.Unisex, ProductColor color = ProductColor.Black) =>
        new(Guid.NewGuid(), name, audience, AnyTraits with { Color = color });

    /// <summary>A scorer that looks the score up by the candidate's color, so a test can state each candidate's score directly.</summary>
    private static Func<ProductTraits, int> ScoresByColor(Dictionary<ProductColor, int> scores) => traits => scores[traits.Color];

    [Fact]
    public void OnlyScoresOfSeventyAndUpQualify()
    {
        var below = Candidate("below", color: ProductColor.Red);
        var exactly = Candidate("exactly", color: ProductColor.Blue);
        var scorer = ScoresByColor(new() { [ProductColor.Red] = 69, [ProductColor.Blue] = 70 });

        var picks = WeeklyMatchSelection.Select([below, exactly], Audience.Unisex, scorer);

        Assert.Equal(["exactly"], picks.Select(pick => pick.Candidate.DisplayName));
        Assert.Equal(70, Assert.Single(picks).Score);
    }

    [Fact]
    public void AtMostThreeAreChosenHighestScoreFirst()
    {
        var colors = new[] { ProductColor.Black, ProductColor.White, ProductColor.Grey, ProductColor.Navy, ProductColor.Blue };
        var scores = new[] { 75, 99, 80, 71, 90 };
        var candidates = colors.Select((color, index) => Candidate($"p{index}", color: color)).ToList();
        var scorer = ScoresByColor(colors.Zip(scores).ToDictionary(pair => pair.First, pair => pair.Second));

        var picks = WeeklyMatchSelection.Select(candidates, Audience.Unisex, scorer);

        Assert.Equal(["p1", "p4", "p2"], picks.Select(pick => pick.Candidate.DisplayName));
        Assert.Equal([99, 90, 80], picks.Select(pick => pick.Score));
    }

    [Fact]
    public void EqualScoresKeepTheNewestFirstOrderOfTheInput()
    {
        var candidates = Enumerable.Range(0, 5).Select(index => Candidate($"p{index}")).ToList();

        var picks = WeeklyMatchSelection.Select(candidates, Audience.Unisex, _ => 85);

        Assert.Equal(["p0", "p1", "p2"], picks.Select(pick => pick.Candidate.DisplayName));
    }

    [Fact]
    public void NothingQualifyingMeansNoPicks() =>
        Assert.Empty(WeeklyMatchSelection.Select([Candidate("a"), Candidate("b")], Audience.Unisex, _ => 40));

    [Theory]
    [InlineData("Women", "Women", true)]
    [InlineData("Women", "Unisex", true)]
    [InlineData("Women", "Men", false)]
    [InlineData("Women", "Kids", false)]
    [InlineData("Men", "Men", true)]
    [InlineData("Men", "Women", false)]
    [InlineData("Unisex", "Men", true)]
    [InlineData("Unisex", "Women", true)]
    [InlineData("Unisex", "Unisex", true)]
    [InlineData("Unisex", "Kids", false)]
    public void ProductsAreOnlyForTheirAudience(string user, string product, bool expected) =>
        Assert.Equal(expected, WeeklyMatchSelection.AudienceMatches(Enum.Parse<Audience>(user), Enum.Parse<Audience>(product)));

    [Fact]
    public void SelectionSkipsOtherAudiences()
    {
        var picks = WeeklyMatchSelection.Select([Candidate("men", Audience.Men), Candidate("women", Audience.Women)], Audience.Women, _ => 90);

        Assert.Equal(["women"], picks.Select(pick => pick.Candidate.DisplayName));
    }

    [Theory]
    [InlineData(2026, 11, 1, 12, 0, "2026-W44")]
    [InlineData(2026, 11, 2, 12, 0, "2026-W45")]
    [InlineData(2026, 11, 1, 23, 30, "2026-W45")]
    [InlineData(2026, 12, 31, 12, 0, "2026-W53")]
    [InlineData(2027, 1, 1, 12, 0, "2026-W53")]
    [InlineData(2027, 1, 4, 12, 0, "2027-W01")]
    [InlineData(2026, 1, 1, 0, 30, "2026-W01")]
    public void TheIsoWeekIsReadInIstanbulTime(int year, int month, int day, int hour, int minute, string expected)
    {
        var utc = new DateTimeOffset(year, month, day, hour, minute, 0, TimeSpan.Zero);

        Assert.Equal(expected, WeeklyMatchSelection.IsoWeekKey(utc, QuietHours.Zone));
    }

    [Fact]
    public void TheDedupeKeyNamesThePersonaAndTheWeek()
    {
        var personaId = Guid.NewGuid();

        Assert.Equal($"newmatches:{personaId}:2026-W44", WeeklyMatchSelection.DedupeKey(personaId, "2026-W44"));
    }
}
