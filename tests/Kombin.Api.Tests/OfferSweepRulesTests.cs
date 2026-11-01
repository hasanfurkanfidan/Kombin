using Kombin.Api.Features.Catalog;
using Kombin.Api.Persistence.Entities.Catalog;

namespace Kombin.Api.Tests;

public class OfferSweepRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 4, 30, 0, TimeSpan.Zero);

    private static Offer ActiveOffer(DateTimeOffset lastSeenAt) =>
        new()
        {
            ProductId = Guid.NewGuid(),
            CurrentPrice = 499.90m,
            OriginalPrice = 699.90m,
            StockStatus = StockStatus.InStock,
            LastSeenAt = lastSeenAt,
            IsActive = true,
        };

    private static bool IsStale(Offer offer) => OfferSweepRules.Stale(Now).Compile()(offer);

    [Fact]
    public void AnOfferSeenExactlyFortyEightHoursAgoIsStillFresh()
    {
        Assert.False(IsStale(ActiveOffer(Now - TimeSpan.FromHours(48))));
    }

    [Fact]
    public void AnOfferSeenJustOverFortyEightHoursAgoIsStale()
    {
        Assert.True(IsStale(ActiveOffer(Now - TimeSpan.FromHours(48) - TimeSpan.FromTicks(1))));
        Assert.True(IsStale(ActiveOffer(Now - TimeSpan.FromDays(3))));
    }

    [Fact]
    public void AnOfferSeenRecentlyIsFresh()
    {
        Assert.False(IsStale(ActiveOffer(Now - TimeSpan.FromHours(47))));
        Assert.False(IsStale(ActiveOffer(Now)));
    }

    [Fact]
    public void AnOfferThatIsAlreadyInactiveIsNeverSweptAgain()
    {
        var offer = ActiveOffer(Now - TimeSpan.FromDays(30));
        offer.IsActive = false;

        Assert.False(IsStale(offer));
    }

    [Fact]
    public void DeactivatingTakesTheOfferOutOfSaleAndOutOfStock()
    {
        var offer = ActiveOffer(Now - TimeSpan.FromDays(3));

        OfferSweepRules.Deactivate(offer, Now);

        Assert.False(offer.IsActive);
        Assert.Equal(StockStatus.OutOfStock, offer.StockStatus);
        Assert.Equal(499.90m, offer.CurrentPrice);
        Assert.Equal(Now - TimeSpan.FromDays(3), offer.LastSeenAt);
    }

    [Fact]
    public void DeactivatingWritesAClosingOutOfStockSnapshotAtTheSamePrice()
    {
        var offer = ActiveOffer(Now - TimeSpan.FromDays(3));

        var snapshot = OfferSweepRules.Deactivate(offer, Now);

        Assert.Equal(offer.Id, snapshot.OfferId);
        Assert.Equal(499.90m, snapshot.Price);
        Assert.Equal(699.90m, snapshot.OriginalPrice);
        Assert.Equal(StockStatus.OutOfStock, snapshot.StockStatus);
        Assert.Equal(Now, snapshot.ObservedAt);
    }

    private static Product ProductWith(bool isActive, params bool[] offersActive)
    {
        var product = new Product { IsActive = isActive };
        product.Offers.AddRange(offersActive.Select(active => new Offer { IsActive = active }));

        return product;
    }

    [Fact]
    public void AProductThatLostItsLastActiveOfferIsDeactivated()
    {
        var product = ProductWith(isActive: true, false, false);

        Assert.True(OfferSweepRules.RepairActivity(product));
        Assert.False(product.IsActive);
    }

    [Fact]
    public void AProductWithoutAnyOfferIsDeactivated()
    {
        var product = ProductWith(isActive: true);

        Assert.True(OfferSweepRules.RepairActivity(product));
        Assert.False(product.IsActive);
    }

    [Fact]
    public void AnInactiveProductWithAnActiveOfferIsReactivated()
    {
        var product = ProductWith(isActive: false, false, true);

        Assert.True(OfferSweepRules.RepairActivity(product));
        Assert.True(product.IsActive);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false, true)]
    [InlineData(false, false)]
    [InlineData(false)]
    public void AProductThatAlreadyMatchesItsOffersIsLeftAlone(bool isActive, params bool[] offersActive)
    {
        var product = ProductWith(isActive, offersActive);

        Assert.False(OfferSweepRules.RepairActivity(product));
        Assert.Equal(isActive, product.IsActive);
    }
}
