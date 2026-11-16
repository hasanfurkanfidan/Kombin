using System.Data.Common;
using Kombin.Api.Features.Catalog.Jobs;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Catalog;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static Kombin.Api.IntegrationTests.CatalogClientExtensions;

namespace Kombin.Api.IntegrationTests;

/// <summary>
/// Night review item 7: a worker sights a listing while the sweep is between reading it as stale and writing it off. The race is made on purpose:
/// right after the sweep's first read of the offers, another connection does what an upsert does to the row (<c>last_seen_at = now</c>).
/// </summary>
public sealed class OfferSweepRaceTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Runs <see cref="Action"/> once, right after the first read of the offers table that goes through the context it is registered on.</summary>
    private sealed class SightingInterceptor : DbCommandInterceptor
    {
        private int _fired;

        public Func<Task>? Action { get; set; }

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (Action is { } action
                && command.CommandText.Contains("catalog.offers", StringComparison.Ordinal)
                && command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
                && Interlocked.Exchange(ref _fired, 1) == 0)
            {
                await action();
            }

            return result;
        }
    }

    [Fact]
    public async Task AnOfferSightedWhileTheSweepRunsIsNotDeactivatedAndGetsNoClosingSnapshot()
    {
        using var client = factory.CreateClient();
        var (sellerSlug, sellerId) = await client.CreateSellerAsync("Race", Ct);
        var brand = Unique("Race");
        var seen = $"seen-{Guid.NewGuid():N}";
        var gone = $"gone-{Guid.NewGuid():N}";
        await client.UpsertOkAsync(sellerSlug, [Item(seen, 300m, brand, "Seen Blazer"), Item(gone, 400m, brand, "Gone Blazer")], Ct);
        await factory.AgeOfferAsync(sellerSlug, seen, TimeSpan.FromDays(3), Ct);
        await factory.AgeOfferAsync(sellerSlug, gone, TimeSpan.FromDays(3), Ct);
        var seenId = await factory.WithDbAsync(db => db.Offers.Where(offer => offer.Seller.Slug == sellerSlug && offer.ExternalId == seen).Select(offer => offer.Id).SingleAsync(Ct));

        var interceptor = new SightingInterceptor();
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.ConfigureDbContext<KombinDbContext>(options => options.AddInterceptors(interceptor))));
        var connectionString = host.Services.GetRequiredService<IConfiguration>().GetConnectionString("Postgres")!;
        interceptor.Action = async () =>
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(Ct);
            await using var command = new NpgsqlCommand("UPDATE catalog.offers SET last_seen_at = now(), updated_at = now() WHERE id = @id", connection);
            command.Parameters.AddWithValue("id", seenId);
            await command.ExecuteNonQueryAsync(Ct);
        };

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var summary = await scope.ServiceProvider.GetRequiredService<OfferSweepJob>().SweepAsync(sellerId, Ct);

            // Only the listing nobody saw is written off.
            Assert.Equal(1, summary.OffersDeactivated);
        }

        var offers = await factory.WithDbAsync(db => db.Offers.AsNoTracking().Where(offer => offer.Seller.Slug == sellerSlug).ToListAsync(Ct));
        var sighted = offers.Single(offer => offer.ExternalId == seen);
        Assert.True(sighted.IsActive);
        Assert.Equal(StockStatus.InStock, sighted.StockStatus);
        Assert.False(offers.Single(offer => offer.ExternalId == gone).IsActive);

        // The price history agrees: the sighted offer has no out-of-stock closing row, the deactivated one has exactly one.
        var snapshots = await factory.WithDbAsync(db => db.PriceSnapshots.AsNoTracking().Where(snapshot => snapshot.Offer.Seller.Slug == sellerSlug).ToListAsync(Ct));
        Assert.DoesNotContain(snapshots, snapshot => snapshot.OfferId == seenId && snapshot.StockStatus == StockStatus.OutOfStock);
        Assert.Single(snapshots, snapshot => snapshot.OfferId != seenId && snapshot.StockStatus == StockStatus.OutOfStock);
    }
}
