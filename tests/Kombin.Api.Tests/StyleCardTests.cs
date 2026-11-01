using Kombin.Api.Features.Catalog;
using Kombin.Api.Features.Profile;
using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Profile;
using Kombin.Api.Persistence.Seed;

namespace Kombin.Api.Tests;

/// <summary>Guards the hand-written <c>style-cards.json</c> and the feed logic. A typo in the file would otherwise surface in production only.</summary>
public class StyleCardTests
{
    private static readonly IReadOnlyList<StyleCard> Cards = StyleCardCatalog.Load().ToEntities();

    [Fact]
    public void HasFortyCardsSplitSixteenSixteenEight()
    {
        Assert.Equal(40, Cards.Count);
        Assert.Equal(16, Cards.Count(card => card.Audience == Audience.Women));
        Assert.Equal(16, Cards.Count(card => card.Audience == Audience.Men));
        Assert.Equal(8, Cards.Count(card => card.Audience == Audience.Unisex));
    }

    [Fact]
    public void EveryFamilyAppearsAtLeastThreeTimesAndEveryAudienceCoversAllFamilies()
    {
        foreach (var family in Enum.GetValues<StyleFamily>())
        {
            Assert.True(Cards.Count(card => card.Family == family) >= 3, $"{family} has too few cards");
        }

        foreach (var audience in new[] { Audience.Women, Audience.Men })
        {
            var families = Cards.Where(card => card.Audience == audience || card.Audience == Audience.Unisex).Select(card => card.Family).Distinct();

            Assert.Equal(Enum.GetValues<StyleFamily>().Length, families.Count());
        }
    }

    [Fact]
    public void IdsAreFixedAndUniqueAndSortOrdersFollowTheFile()
    {
        Assert.Equal(Cards.Count, Cards.Select(card => card.Id).Distinct().Count());
        Assert.All(Cards, card => Assert.NotEqual(Guid.Empty, card.Id));
        Assert.Equal(Enumerable.Range(1, Cards.Count), Cards.Select(card => card.SortOrder));
    }

    [Fact]
    public void FieldsFitTheirColumnsAndUseKnownNames()
    {
        Assert.All(Cards, card =>
        {
            Assert.InRange(card.Title.Length, 1, 80);
            Assert.InRange(card.ImageUrl.Length, 1, 2048);
            Assert.StartsWith("https://picsum.photos/seed/style-", card.ImageUrl, StringComparison.Ordinal);
            Assert.InRange(card.Formality, 1, 5);
            Assert.NotEmpty(card.Colors);
            Assert.All(card.Colors, color => Assert.True(EnumNames.IsValid<ProductColor>(color), $"{card.Title}: color {color}"));
            Assert.NotEmpty(card.Fits);
            Assert.All(card.Fits, fit => Assert.True(EnumNames.IsValid<ProductFit>(fit) && fit != nameof(ProductFit.Unknown), $"{card.Title}: fit {fit}"));
            Assert.NotEqual(Season.None, card.Seasons);
            Assert.NotEqual(Audience.Kids, card.Audience);
            Assert.True(card.IsActive);
        });
    }

    [Fact]
    public void ImagesAreNumberedPlaceholders()
    {
        Assert.Equal("https://picsum.photos/seed/style-1/600/800", Cards[0].ImageUrl);
        Assert.Equal(Cards.Count, Cards.Select(card => card.ImageUrl).Distinct().Count());
    }

    [Theory]
    [InlineData("Unisex", "Women", true)]
    [InlineData("Unisex", "Men", true)]
    [InlineData("Unisex", "Unisex", true)]
    [InlineData("Women", "Women", true)]
    [InlineData("Women", "Unisex", true)]
    [InlineData("Women", "Men", false)]
    [InlineData("Men", "Women", false)]
    [InlineData("Men", "Unisex", true)]
    public void AudienceDecidesWhichCardsAProfileSees(string profile, string card, bool visible)
    {
        Assert.Equal(visible, StyleCardFeed.IsVisibleTo(Enum.Parse<Audience>(profile), Enum.Parse<Audience>(card)));
    }

    [Fact]
    public void TheFirstEightCardsOfAnyAudienceCoverEveryFamily()
    {
        foreach (var profile in new[] { Audience.Women, Audience.Men, Audience.Unisex })
        {
            var feed = StyleCardOrdering.Order([.. Cards.Where(card => StyleCardFeed.IsVisibleTo(profile, card.Audience))], profile);

            Assert.Equal(Enum.GetValues<StyleFamily>().Length, feed.Take(8).Select(card => card.Family).Distinct().Count());
            Assert.Equal(feed.Count, feed.Select(card => card.Id).Distinct().Count());
        }
    }

    [Fact]
    public void TheRealDeckRotatesAudiencesForAUnisexProfile()
    {
        var feed = StyleCardOrdering.Order(Cards, Audience.Unisex);

        Assert.True(feed.Take(6).Select(card => card.Audience).Distinct().Count() >= 2);
        Assert.Equal(3, feed.Take(12).Select(card => card.Audience).Distinct().Count());
    }

    private static List<StyleCard> Deck(int women, int men, int unisex)
    {
        var families = Enum.GetValues<StyleFamily>();
        var order = 0;
        var cards = new List<StyleCard>();

        foreach (var (audience, count) in new[] { (Audience.Women, women), (Audience.Men, men), (Audience.Unisex, unisex) })
        {
            for (var index = 0; index < count; index++)
            {
                cards.Add(new() { Id = Guid.NewGuid(), Audience = audience, Family = families[index % families.Length], SortOrder = ++order });
            }
        }

        return cards;
    }

    [Fact]
    public void AUnisexProfileRotatesAudiencesAndStillCoversEveryFamilyEarly()
    {
        var cards = Deck(women: 16, men: 16, unisex: 8);

        var feed = StyleCardOrdering.Order(cards, Audience.Unisex);

        Assert.True(feed.Take(6).Select(card => card.Audience).Distinct().Count() >= 2);
        Assert.Equal(3, feed.Take(12).Select(card => card.Audience).Distinct().Count());
        Assert.Equal(Enum.GetValues<StyleFamily>().Length, feed.Take(8).Select(card => card.Family).Distinct().Count());
        Assert.Equal(cards.Select(card => card.Id).Order(), feed.Select(card => card.Id).Order());
        Assert.Equal(feed, StyleCardOrdering.Order(cards, Audience.Unisex));
        Assert.Equal(feed, StyleCardOrdering.Order([.. cards.AsEnumerable().Reverse()], Audience.Unisex));
    }

    [Theory]
    [InlineData("Women")]
    [InlineData("Men")]
    public void AGenderedProfileAlternatesItsOwnAudienceWithUnisex(string audience)
    {
        var profile = Enum.Parse<Audience>(audience);
        var cards = Deck(women: profile == Audience.Women ? 16 : 0, men: profile == Audience.Men ? 16 : 0, unisex: 8);

        var feed = StyleCardOrdering.Order(cards, profile);

        Assert.Equal([profile, Audience.Unisex, profile, Audience.Unisex], feed.Take(4).Select(card => card.Audience));
        Assert.Equal(cards.Count, feed.Count);
    }

    [Fact]
    public void WhenAnAudienceRunsOutTheOthersContinueAndNothingIsLost()
    {
        var cards = Deck(women: 2, men: 9, unisex: 1);

        var feed = StyleCardOrdering.Order(cards, Audience.Unisex);

        Assert.Equal(cards.Count, feed.Count);
        Assert.Equal(cards.Select(card => card.Id).Order(), feed.Select(card => card.Id).Order());
        Assert.Empty(StyleCardOrdering.Order([], Audience.Unisex));
    }

    [Fact]
    public void SortOrderDecidesInsideAFamilyAndAudience()
    {
        StyleCard Make(StyleFamily family, int order) => new() { Id = Guid.NewGuid(), Family = family, Audience = Audience.Women, SortOrder = order };
        var a1 = Make(StyleFamily.Minimal, 1);
        var a2 = Make(StyleFamily.Minimal, 2);
        var a3 = Make(StyleFamily.Minimal, 3);
        var b1 = Make(StyleFamily.Street, 4);

        var feed = StyleCardOrdering.Order([a3, b1, a2, a1], Audience.Women);

        Assert.Equal([a1, b1, a2, a3], feed);
    }
}
