using Kombin.Api.Persistence.Entities.Profile;
using Npgsql;

namespace Kombin.Api.Persistence.Seed;

/// <summary>
/// Brings <c>profile.style_cards</c> in line with <c>style-cards.json</c>. Unlike <see cref="DevSeeder"/> this runs in every
/// environment: the cards are content the swipe onboarding cannot work without. Idempotent; cards are matched by their fixed ID, and a
/// card removed from the file is switched off rather than deleted, so swipes that point to it stay valid.
/// </summary>
internal sealed partial class StyleCardSeeder(KombinDbContext db, ILogger<StyleCardSeeder> logger)
{
    public Task SeedAsync(CancellationToken cancellationToken = default) => SeedAsync(StyleCardCatalog.Load(), cancellationToken);

    public async Task SeedAsync(StyleCardCatalog catalog, CancellationToken cancellationToken = default)
    {
        var wanted = catalog.ToEntities();
        var existing = await db.StyleCards.ToDictionaryAsync(card => card.Id, cancellationToken);
        var inserted = 0;

        foreach (var card in wanted)
        {
            if (existing.TryGetValue(card.Id, out var stored))
            {
                Apply(stored, card);
            }
            else
            {
                db.StyleCards.Add(card);
                inserted++;
            }
        }

        var wantedIds = wanted.Select(card => card.Id).ToHashSet();
        foreach (var stored in existing.Values.Where(card => card.IsActive && !wantedIds.Contains(card.Id)))
        {
            stored.IsActive = false;
        }

        db.ChangeTracker.DetectChanges();
        var updated = db.ChangeTracker.Entries<StyleCard>().Count(entry => entry.State == EntityState.Modified);

        if (inserted == 0 && updated == 0)
        {
            LogAlreadySeeded(logger, wanted.Count);
            return;
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            LogSeeded(logger, inserted, updated);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Another instance seeded the same cards while this one was starting; the result is the same.
            LogLostRace(logger);
        }
        finally
        {
            db.ChangeTracker.Clear();
        }
    }

    private static void Apply(StyleCard target, StyleCard source)
    {
        target.Title = source.Title;
        target.ImageUrl = source.ImageUrl;
        target.Audience = source.Audience;
        target.Family = source.Family;
        target.Colors = source.Colors;
        target.Formality = source.Formality;
        target.Fits = source.Fits;
        target.Seasons = source.Seasons;
        target.SortOrder = source.SortOrder;
        target.IsActive = source.IsActive;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Style cards: inserted {Inserted}, updated {Updated}.")]
    private static partial void LogSeeded(ILogger logger, int inserted, int updated);

    [LoggerMessage(Level = LogLevel.Information, Message = "Style cards: already up to date ({Cards} cards).")]
    private static partial void LogAlreadySeeded(ILogger logger, int cards);

    [LoggerMessage(Level = LogLevel.Information, Message = "Style cards: another instance seeded them first.")]
    private static partial void LogLostRace(ILogger logger);
}
