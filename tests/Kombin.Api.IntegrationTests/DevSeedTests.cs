using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kombin.Api.Common.Time;
using Kombin.Api.Features.Catalog.UpsertOffers;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Identity;
using Kombin.Api.Persistence.Seed;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static Kombin.Api.IntegrationTests.CatalogClientExtensions;

namespace Kombin.Api.IntegrationTests;

public sealed class DevSeedTests(SeededApiFactory factory) : IClassFixture<SeededApiFactory>
{
    private static readonly DevCatalog Catalog = DevCatalog.Load();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Counts(int Brands, int Products, int Offers, int Images, int Snapshots, int Users, int Sellers);

    private async Task<Counts> CountAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KombinDbContext>();

        return new Counts(
            await db.Brands.CountAsync(Ct),
            await db.Products.CountAsync(Ct),
            await db.Offers.CountAsync(Ct),
            await db.ProductImages.CountAsync(Ct),
            await db.PriceSnapshots.CountAsync(Ct),
            await db.Users.CountAsync(Ct),
            await db.Sellers.CountAsync(Ct));
    }

    [Fact]
    public async Task StartupSeedsExactlyWhatTheSeedFileDescribes()
    {
        var counts = await CountAsync();
        var listings = Catalog.Listings();

        Assert.Equal(Catalog.Products.Select(product => product.Brand).Distinct().Count(), counts.Brands);
        Assert.Equal(Catalog.Products.Count, counts.Products);
        Assert.Equal(listings.Count, counts.Offers);
        Assert.Equal(Catalog.Products.Sum(product => product.Images), counts.Images);
        Assert.Equal(1, counts.Users);

        // The two marketplaces come from the Catalog migration; every brand with a store offer gets one store.
        Assert.Equal(2 + listings.Select(listing => listing.SellerSlug).Where(slug => slug.EndsWith("-store", StringComparison.Ordinal)).Distinct().Count(), counts.Sellers);

        // Each offer has its upsert snapshot plus a backfilled history of 1-4 day steps over 90 days.
        Assert.InRange(counts.Snapshots, listings.Count * 24, listings.Count * 91);
    }

    [Fact]
    public async Task SeedingASecondTimeChangesNothingAndLogsAlreadySeeded()
    {
        var before = await CountAsync();
        var logger = new ListLogger<DevSeeder>();

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var services = scope.ServiceProvider;
            var seeder = new DevSeeder(
                services.GetRequiredService<KombinDbContext>(),
                services.GetRequiredService<OfferUpserter>(),
                services.GetRequiredService<IPasswordHasher<User>>(),
                services.GetRequiredService<IClock>(),
                logger);

            await seeder.SeedAsync(Ct);
        }

        Assert.Equal(before, await CountAsync());
        Assert.Contains(logger.Messages, message => message.StartsWith("Seed catalog: already seeded", StringComparison.Ordinal));
        Assert.Contains(logger.Messages, message => message.StartsWith("Seed price history: already seeded", StringComparison.Ordinal));
        Assert.Contains(logger.Messages, message => message.StartsWith("Seed users: already seeded", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ChangesInTheSeedFileAreUpsertedOnTheNextRun()
    {
        var first = Catalog.Products[0];
        var changedOffer = first.Offers[0] with { Price = first.Offers[0].Price + 100m };
        var changed = Catalog with { Products = [first with { Offers = [changedOffer, .. first.Offers.Skip(1)] }, .. Catalog.Products.Skip(1)] };
        var externalId = changed.Listings()[0].Item.ExternalId;

        async Task<(decimal Price, int Snapshots)> OfferStateAsync()
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<KombinDbContext>();
            var offer = await db.Offers.SingleAsync(candidate => candidate.ExternalId == externalId, Ct);
            return (offer.CurrentPrice, await db.PriceSnapshots.CountAsync(snapshot => snapshot.OfferId == offer.Id, Ct));
        }

        async Task SeedAsync(DevCatalog catalog)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<DevSeeder>().SeedAsync(catalog, Ct);
        }

        var before = await OfferStateAsync();
        var counts = await CountAsync();

        await SeedAsync(changed);
        var after = await OfferStateAsync();

        Assert.Equal(first.Offers[0].Price + 100m, after.Price);
        Assert.Equal(before.Snapshots + 1, after.Snapshots);
        Assert.Equal(counts with { Snapshots = counts.Snapshots + 1 }, await CountAsync());

        // Put the shared database back so the other tests see the original prices.
        await SeedAsync(Catalog);
        Assert.Equal(first.Offers[0].Price, (await OfferStateAsync()).Price);
    }

    [Fact]
    public async Task TheDemoUserCanLogInWithTheDocumentedCredentials()
    {
        using var client = factory.CreateClient();

        using var response = await client.LoginAsync(DevSeeder.DemoEmail, DevSeeder.DemoPassword, cancellationToken: Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var tokens = await response.ReadTokensAsync(Ct);

        using var me = await client.GetMeAsync(tokens.AccessToken, cancellationToken: Ct);
        var body = await me.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("demo@kombin.app", body.GetProperty("email").GetString());
        Assert.Equal("Demo Kullanıcı", body.GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task TheProductListHidesProductsWithOnlyOutOfStockOffersButTheirDetailStillOpens()
    {
        using var client = factory.CreateClient();
        var sellable = Catalog.Products.Where(product => product.IsSellable).ToList();
        var hidden = Catalog.Products.Where(product => !product.IsSellable).ToList();

        var list = await client.GetJsonAsync("/api/v1/catalog/products?pageSize=100", Ct);

        Assert.Equal(sellable.Count, list.GetProperty("totalCount").GetInt32());
        var listed = list.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("name").GetString()).ToList();
        Assert.Equal(sellable.Count, listed.Count);
        Assert.All(hidden, product => Assert.DoesNotContain(product.Name, listed));

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KombinDbContext>();

        foreach (var product in hidden)
        {
            var id = (await db.Products.SingleAsync(candidate => candidate.ModelCode == product.ModelCode, Ct)).Id;
            var detail = await client.GetJsonAsync($"/api/v1/catalog/products/{id}", Ct);

            Assert.All(detail.GetProperty("offers").EnumerateArray(), offer => Assert.Equal("OutOfStock", offer.GetProperty("stockStatus").GetString()));
            Assert.Equal(JsonValueKind.Null, detail.GetProperty("priceSummary").GetProperty("lowestNow").ValueKind);
        }
    }

    [Fact]
    public async Task EveryLeafCategoryStillListsAtLeastTwoProducts()
    {
        using var client = factory.CreateClient();

        foreach (var leaf in Catalog.Products.Select(product => product.Category).Distinct())
        {
            var list = await client.GetJsonAsync($"/api/v1/catalog/products?categorySlug={leaf}&pageSize=100", Ct);

            Assert.True(list.GetProperty("totalCount").GetInt32() >= 2, leaf);
        }
    }

    [Fact]
    public async Task ADetailShowsSeveralOffersByEffectivePriceAndNinetyDaysOfHistory()
    {
        using var client = factory.CreateClient();
        var list = await client.GetJsonAsync("/api/v1/catalog/products?query=Basic%20Bisiklet&pageSize=5", Ct);
        var id = Assert.Single(list.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid();

        var detail = await client.GetJsonAsync($"/api/v1/catalog/products/{id}", Ct);
        var offers = detail.GetProperty("offers").EnumerateArray().ToList();

        Assert.Equal(3, offers.Count);
        Assert.Equal(
            ["hepsiburada", "mavi-store", "trendyol"],
            offers.Select(offer => offer.GetProperty("seller").GetProperty("slug").GetString()).Order(StringComparer.Ordinal));
        Assert.Equal(
            offers.Select(offer => offer.GetProperty("effectivePrice").GetDecimal()).Order(),
            offers.Select(offer => offer.GetProperty("effectivePrice").GetDecimal()));
        Assert.Equal(2, detail.GetProperty("images").GetArrayLength());

        var summary = detail.GetProperty("priceSummary");
        Assert.True(summary.GetProperty("highestLast90Days").GetDecimal() > summary.GetProperty("lowestLast90Days").GetDecimal());

        var history = await client.GetJsonAsync($"/api/v1/catalog/products/{id}/price-history?days=90", Ct);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        foreach (var series in history.EnumerateArray())
        {
            var points = series.GetProperty("points").EnumerateArray().ToList();
            var first = DateOnly.Parse(points[0].GetProperty("date").GetString()!, System.Globalization.CultureInfo.InvariantCulture);

            Assert.InRange(points.Count, 24, 91);
            Assert.True(first <= today.AddDays(-85), $"history starts {first}");
        }
    }

    [Fact]
    public async Task AboutAQuarterOfTheListedProductsEndAtTheirNinetyDayLow()
    {
        using var client = factory.CreateClient();
        var indexByModelCode = Catalog.Products.Select((product, index) => (product.ModelCode, index)).ToDictionary(pair => pair.ModelCode, pair => pair.index);

        var list = await client.GetJsonAsync("/api/v1/catalog/products?pageSize=100", Ct);
        var atLowest = 0;
        var listed = 0;

        foreach (var item in list.GetProperty("items").EnumerateArray())
        {
            var detail = await client.GetJsonAsync($"/api/v1/catalog/products/{item.GetProperty("id").GetGuid()}", Ct);
            var isLowest = detail.GetProperty("priceSummary").GetProperty("isLowestOfLast90Days").GetBoolean();
            var index = indexByModelCode[detail.GetProperty("modelCode").GetString()!];

            // The seeder generates every fourth product (by position in the file) at its low and gives all others a cheaper day.
            Assert.Equal(index % 4 == 0, isLowest);
            listed++;
            atLowest += isLowest ? 1 : 0;
        }

        Assert.InRange(atLowest / (double)listed, 0.2, 0.3);
    }

    [Fact]
    public async Task SomeHistoryPointsAreOutOfStock()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KombinDbContext>();

        var outOfStock = await db.PriceSnapshots.CountAsync(snapshot => snapshot.StockStatus == StockStatus.OutOfStock, Ct);
        var total = await db.PriceSnapshots.CountAsync(Ct);

        Assert.InRange(outOfStock / (double)total, 0.01, 0.15);
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
