using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Seed;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Kombin.Api.IntegrationTests;

/// <summary>Runs on the seeded host's own database, so the cards can be edited without disturbing other tests.</summary>
public sealed class StyleCardSeedTests(SeededApiFactory factory) : IClassFixture<SeededApiFactory>
{
    private static readonly StyleCardCatalog Catalog = StyleCardCatalog.Load();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(int Total, int Active)> CountAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KombinDbContext>();

        return (await db.StyleCards.CountAsync(Ct), await db.StyleCards.CountAsync(card => card.IsActive, Ct));
    }

    private async Task SeedAsync(StyleCardCatalog catalog)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<StyleCardSeeder>().SeedAsync(catalog, Ct);
    }

    [Fact]
    public async Task StartupSeedsExactlyTheCardsOfTheFile()
    {
        Assert.Equal((Catalog.Cards.Count, Catalog.Cards.Count), await CountAsync());
    }

    [Fact]
    public async Task SeedingAgainChangesNothing()
    {
        await SeedAsync(Catalog);
        await SeedAsync(Catalog);

        Assert.Equal((Catalog.Cards.Count, Catalog.Cards.Count), await CountAsync());
    }

    [Fact]
    public async Task EditsOfTheFileReachExistingCardsWithoutChangingTheirIds()
    {
        var edited = Catalog with { Cards = [.. Catalog.Cards.Select((card, index) => index == 0 ? card with { Title = "Düzenlenmiş başlık", ImageUrl = "https://img.example.com/real.jpg" } : card)] };

        try
        {
            await SeedAsync(edited);

            await using var scope = factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<KombinDbContext>();
            var card = await db.StyleCards.AsNoTracking().SingleAsync(candidate => candidate.Id == Catalog.Cards[0].Id, Ct);
            Assert.Equal("Düzenlenmiş başlık", card.Title);
            Assert.Equal("https://img.example.com/real.jpg", card.ImageUrl);
            Assert.Equal(Catalog.Cards.Count, await db.StyleCards.CountAsync(Ct));
        }
        finally
        {
            await SeedAsync(Catalog);
        }
    }

    [Fact]
    public async Task ACardRemovedFromTheFileIsSwitchedOffNotDeleted()
    {
        var shorter = Catalog with { Cards = [.. Catalog.Cards.SkipLast(1)] };

        try
        {
            await SeedAsync(shorter);

            Assert.Equal((Catalog.Cards.Count, Catalog.Cards.Count - 1), await CountAsync());
        }
        finally
        {
            await SeedAsync(Catalog);
        }

        Assert.Equal((Catalog.Cards.Count, Catalog.Cards.Count), await CountAsync());
    }
}
