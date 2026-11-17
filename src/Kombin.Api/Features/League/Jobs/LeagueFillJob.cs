using System.Globalization;
using Hangfire;
using Kombin.Api.Common.Time;
using Kombin.Api.Features.League.Rules;
using Kombin.Api.Features.Recommendation;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.League;
using Kombin.Api.Persistence.Entities.Recommendation;
using Microsoft.Extensions.Options;

namespace Kombin.Api.Features.League.Jobs;

/// <summary>What one fill did: buckets looked at, engine entries added.</summary>
internal sealed record LeagueFillSummary(int Buckets, int Added);

/// <summary>
/// <c>league-fill</c>, every 15 minutes from 00:00 to 20:45 Istanbul (design note section 6): "az oyunculu günlerde havuzu öneri motorunun
/// kombinleri doldurur". For each audience (Women, Men) and occasion bucket that has a competing human entry today, while the bucket's
/// competing human entries are fewer than <c>League:EngineFill:MinPool</c>, engine outfits top the pool up to that size, at most
/// <c>League:EngineFill:MaxPerRun</c> per bucket per run, from Recommendation's <see cref="CatalogOutfitSampler"/> (seed = day number + bucket, so
/// every run of a day draws the same list and adds the outfits not in the pool yet). Engine entries pair like any other and are never ranked or
/// listed; their pieces are catalog products with the product image. A bucket without a human entry today gets nothing: every pair needs a human
/// outfit, so engine outfits there would only crowd the candidate lists (yesterday's buckets are not filled ahead: a new bucket is filled by
/// the next run, at most 15 minutes after its first entry). Does nothing while <c>League:Enabled</c> is false; tests call
/// <see cref="FillAsync"/> with their own day.
/// </summary>
internal sealed partial class LeagueFillJob(KombinDbContext db, IClock clock, IOptions<LeagueOptions> options, ILogger<LeagueFillJob> logger)
{
    public const string JobId = "league-fill";

    public const string Cron = "*/15 0-20 * * *";

    private static readonly Audience[] Audiences = [Audience.Women, Audience.Men];

    [AutomaticRetry(Attempts = 0)]
    [DisableConcurrentExecution(60)]
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        var now = clock.UtcNow;
        var summary = await FillAsync(Istanbul.Today(now), now, cancellationToken);
        if (summary.Added > 0)
        {
            LogFilled(logger, summary.Added, summary.Buckets);
        }
    }

    public async Task<LeagueFillSummary> FillAsync(DateOnly day, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (now >= LeagueDays.ClosesAt(day, settings.DayClosesAt))
        {
            return new LeagueFillSummary(0, 0);
        }

        var buckets = await db.LeagueEntries.AsNoTracking()
            .Where(entry => entry.Kind == LeagueEntryKind.Human && entry.Day == day && entry.Status == LeagueEntryStatus.Competing && Audiences.Contains(entry.Audience))
            .Select(entry => new { entry.Audience, entry.Occasion })
            .Distinct()
            .ToListAsync(cancellationToken);

        var added = 0;
        foreach (var bucket in buckets.OrderBy(bucket => bucket.Audience).ThenBy(bucket => bucket.Occasion))
        {
            added += await FillBucketAsync(day, bucket.Audience, bucket.Occasion, settings, cancellationToken);
        }

        await RecordAsync(day, now, cancellationToken);

        return new LeagueFillSummary(buckets.Count, added);
    }

    private async Task<int> FillBucketAsync(DateOnly day, Audience audience, Occasion? occasion, LeagueOptions settings, CancellationToken cancellationToken)
    {
        var pool = db.LeagueEntries.AsNoTracking()
            .Where(entry => entry.Day == day && entry.Status == LeagueEntryStatus.Competing && entry.Audience == audience && entry.Occasion == occasion);
        var humans = await pool.CountAsync(entry => entry.Kind == LeagueEntryKind.Human, cancellationToken);
        var engineHashes = await pool.Where(entry => entry.Kind == LeagueEntryKind.Engine).Select(entry => entry.PieceSetHash).ToListAsync(cancellationToken);

        var missing = Math.Min(settings.EngineFill.MaxPerRun, settings.EngineFill.MinPool - humans - engineHashes.Count);
        if (humans >= settings.EngineFill.MinPool || missing <= 0)
        {
            return 0;
        }

        var seed = day.DayNumber + ((int)audience * 10) + (occasion is { } chosen ? (int)chosen + 1 : 0);
        IReadOnlyList<SampledOutfit> sampled = await CatalogOutfitSampler.SampleAsync(db, audience, occasion, day, engineHashes.Count + missing, seed, cancellationToken);
        var present = engineHashes.ToHashSet(StringComparer.Ordinal);

        var entries = new List<LeagueEntry>();
        foreach (var outfit in sampled)
        {
            var pieceIds = RepeatRule.PieceIds(outfit.Pieces.Select(piece => (piece.Slot, piece.ProductId)));
            var hash = RepeatRule.Hash(pieceIds);
            if (!present.Add(hash))
            {
                continue;
            }

            var entry = new LeagueEntry
            {
                Day = day,
                Kind = LeagueEntryKind.Engine,
                Audience = audience,
                Occasion = occasion,
                PieceIds = pieceIds,
                PieceSetHash = hash,
                Status = LeagueEntryStatus.Competing,
            };
            entry.Pieces.AddRange(outfit.Pieces.Select((piece, position) => new LeagueEntryPiece
            {
                Position = (short)position,
                Slot = piece.Slot,
                ProductId = piece.ProductId,
                ImageUrl = piece.ImageUrl,
            }));
            entries.Add(entry);

            if (entries.Count == missing)
            {
                break;
            }
        }

        db.LeagueEntries.AddRange(entries);
        await db.SaveChangesAsync(cancellationToken);
        db.ChangeTracker.Clear();

        return entries.Count;
    }

    /// <summary><c>engine_fill_at:{day}</c> in <c>league.state</c>: when the pool was last looked at (no job keeps state in memory).</summary>
    private async Task RecordAsync(DateOnly day, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var key = $"engine_fill_at:{day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";
        var value = now.ToString("O", CultureInfo.InvariantCulture);

        await db.Database.ExecuteSqlAsync(
            $"INSERT INTO league.state (key, value, updated_at) VALUES ({key}, {value}, {now}) ON CONFLICT (key) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at",
            cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "league-fill: {Added} engine outfits added over {Buckets} buckets.")]
    private static partial void LogFilled(ILogger logger, int added, int buckets);
}
