using System.Text;
using Kombin.Api.Common.Time;
using Kombin.Api.Features.Catalog.UpsertOffers;
using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Identity;
using Microsoft.AspNetCore.Identity;

namespace Kombin.Api.Persistence.Seed;

/// <summary>
/// Fills an empty development database with a realistic catalog, 90 days of price history and a demo user.
/// Idempotent: a second run only upserts what the seed file changed. Never registered to run outside Development
/// (see <see cref="PersistenceExtensions.SeedDatabaseOnStartupAsync"/>).
/// </summary>
internal sealed partial class DevSeeder(
    KombinDbContext db,
    OfferUpserter upserter,
    IPasswordHasher<User> passwordHasher,
    IClock clock,
    ILogger<DevSeeder> logger)
{
    public const string DemoEmail = "demo@kombin.app";

    public const string DemoPassword = "Demo1234!";

    public const string DemoDisplayName = "Demo Kullanıcı";

    /// <summary>Fixed so the generated price history is the same on every machine.</summary>
    private const int HistorySeed = 20261101;

    /// <summary>An offer with fewer snapshots than this has no history yet (the upsert itself writes one).</summary>
    private const int MinSnapshotsForHistory = 5;

    /// <summary>Every n-th product (by position in the seed file) is generated at its 90-day low, which is roughly a quarter.</summary>
    private const int LowestEveryNthProduct = 4;

    public Task SeedAsync(CancellationToken cancellationToken = default) => SeedAsync(DevCatalog.Load(), cancellationToken);

    public async Task SeedAsync(DevCatalog catalog, CancellationToken cancellationToken = default)
    {
        var listings = catalog.Listings();

        await SeedCatalogAsync(listings, cancellationToken);
        await BackfillHistoryAsync(listings, cancellationToken);
        await SeedDemoUserAsync(cancellationToken);
    }

    private async Task SeedCatalogAsync(IReadOnlyList<SeedListing> listings, CancellationToken cancellationToken)
    {
        var created = 0;
        var updated = 0;
        var snapshotsWritten = 0;

        // One call per seller, marketplaces first. Brand stores are created on demand by the upserter.
        foreach (var sellerListings in listings.GroupBy(listing => listing.SellerSlug).OrderBy(group => group.Key.EndsWith("-store", StringComparison.Ordinal)))
        {
            var result = await upserter.UpsertAsync(sellerListings.Key, [.. sellerListings.Select(listing => listing.Item)], cancellationToken);

            if (result.IsFailure)
            {
                throw new InvalidOperationException($"Seeding offers of '{sellerListings.Key}' failed: {result.Error.Code}.");
            }

            var outcome = result.Value;

            if (outcome.Failures.Count > 0)
            {
                var details = string.Join("; ", outcome.Failures.Select(failure => $"{failure.ExternalId}: {failure.Error.Code}"));
                throw new InvalidOperationException($"Seeding offers of '{sellerListings.Key}' failed for {outcome.Failures.Count} item(s): {details}");
            }

            created += outcome.Created;
            updated += outcome.Updated;
            snapshotsWritten += outcome.SnapshotsWritten;
        }

        if (created == 0 && snapshotsWritten == 0)
        {
            LogCatalogAlreadySeeded(logger, listings.Count);
        }
        else
        {
            LogCatalogSeeded(logger, created, updated, snapshotsWritten);
        }
    }

    /// <summary>
    /// Direct inserts, not the upserter: the history is invented, so it must not pass through ingestion rules. Per product the
    /// cheapest sellable offer decides whether it ends at its 90-day low (nothing cheaper before) or not (one dip below).
    /// </summary>
    private async Task BackfillHistoryAsync(IReadOnlyList<SeedListing> listings, CancellationToken cancellationToken)
    {
        var externalIds = listings.Select(listing => listing.Item.ExternalId).ToList();

        var offers = (await db.Offers
            .AsNoTracking()
            .Where(offer => externalIds.Contains(offer.ExternalId))
            .Select(offer => new StoredOffer(
                offer.Id,
                offer.Seller.Slug,
                offer.ExternalId,
                offer.CurrentPrice,
                offer.OriginalPrice,
                offer.StockStatus,
                db.PriceSnapshots.Count(snapshot => snapshot.OfferId == offer.Id)))
            .ToListAsync(cancellationToken))
            .ToDictionary(offer => (offer.SellerSlug, offer.ExternalId));

        var now = clock.UtcNow;
        var inserted = 0;
        var skipped = 0;

        foreach (var productListings in listings.GroupBy(listing => listing.ProductIndex))
        {
            var stored = productListings
                .Select(listing => offers[(listing.SellerSlug, listing.Item.ExternalId)])
                .ToList();

            var sellable = stored.Where(offer => offer.StockStatus is StockStatus.InStock or StockStatus.LowStock).ToList();
            var cheapest = sellable.MinBy(offer => offer.CurrentPrice);
            var atLowest = productListings.Key % LowestEveryNthProduct == 0;

            foreach (var offer in stored)
            {
                if (offer.SnapshotCount >= MinSnapshotsForHistory)
                {
                    skipped++;
                    continue;
                }

                var path = PricePathGenerator.Generate(
                    StableSeed(offer.SellerSlug, offer.ExternalId),
                    now,
                    offer.CurrentPrice,
                    offer.OriginalPrice,
                    offer.StockStatus,
                    floor: atLowest && cheapest is not null ? cheapest.CurrentPrice : null,
                    dipBelowCurrent: !atLowest && offer == cheapest);

                db.PriceSnapshots.AddRange(path.Select(point => new PriceSnapshot
                {
                    OfferId = offer.Id,
                    Price = point.Price,
                    OriginalPrice = point.OriginalPrice,
                    StockStatus = point.StockStatus,
                    ObservedAt = point.ObservedAt,
                }));
                inserted += path.Count;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        db.ChangeTracker.Clear();

        if (inserted == 0)
        {
            LogHistoryAlreadySeeded(logger, skipped);
        }
        else
        {
            LogHistorySeeded(logger, inserted, offers.Count - skipped);
        }

    }

    private async Task SeedDemoUserAsync(CancellationToken cancellationToken)
    {
        if (await db.Users.AnyAsync(user => user.Email == DemoEmail, cancellationToken))
        {
            LogUsersAlreadySeeded(logger);
            return;
        }

        var user = new User { Email = DemoEmail, DisplayName = DemoDisplayName };
        user.PasswordHash = passwordHasher.HashPassword(user, DemoPassword);

        db.Users.Add(user);
        await db.SaveChangesAsync(cancellationToken);
        db.ChangeTracker.Clear();

        LogUserSeeded(logger, DemoEmail);
    }

    /// <summary>FNV-1a. <see cref="string.GetHashCode()"/> is randomized per process, which would make the history differ on every start.</summary>
    internal static int StableSeed(string sellerSlug, string externalId)
    {
        var hash = 2166136261u;

        foreach (var value in Encoding.UTF8.GetBytes($"{sellerSlug}/{externalId}"))
        {
            hash = (hash ^ value) * 16777619u;
        }

        return unchecked((int)hash) & int.MaxValue;
    }

    private sealed record StoredOffer(
        Guid Id,
        string SellerSlug,
        string ExternalId,
        decimal CurrentPrice,
        decimal? OriginalPrice,
        StockStatus StockStatus,
        int SnapshotCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "Seed catalog: created {Created}, updated {Updated}, {Snapshots} snapshot(s) written.")]
    private static partial void LogCatalogSeeded(ILogger logger, int created, int updated, int snapshots);

    [LoggerMessage(Level = LogLevel.Information, Message = "Seed catalog: already seeded ({Offers} offers up to date).")]
    private static partial void LogCatalogAlreadySeeded(ILogger logger, int offers);

    [LoggerMessage(Level = LogLevel.Information, Message = "Seed price history: inserted {Snapshots} snapshot(s) for {Offers} offer(s).")]
    private static partial void LogHistorySeeded(ILogger logger, int snapshots, int offers);

    [LoggerMessage(Level = LogLevel.Information, Message = "Seed price history: already seeded ({Offers} offers have history).")]
    private static partial void LogHistoryAlreadySeeded(ILogger logger, int offers);

    [LoggerMessage(Level = LogLevel.Information, Message = "Seed users: created {Email}.")]
    private static partial void LogUserSeeded(ILogger logger, string email);

    [LoggerMessage(Level = LogLevel.Information, Message = "Seed users: already seeded.")]
    private static partial void LogUsersAlreadySeeded(ILogger logger);
}
