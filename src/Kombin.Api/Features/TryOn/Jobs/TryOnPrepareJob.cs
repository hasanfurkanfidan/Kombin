using System.Diagnostics;
using Hangfire;
using Kombin.Api.Common.Storage;
using Kombin.Api.Common.Time;
using Kombin.Api.Features.TryOn.Rules;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.TryOn;
using Microsoft.Extensions.Options;

namespace Kombin.Api.Features.TryOn.Jobs;

/// <summary>What one run did.</summary>
internal sealed record TryOnPrepareSummary(int RendersDeleted, int WardrobePrepared, int CataloguePrepared, int Failures, bool CatalogueBusy);

/// <summary>Limits a run to the tests' own rows (the database is shared); null everywhere in production.</summary>
internal sealed record TryOnScope(IReadOnlyCollection<Guid> WardrobeItemIds, IReadOnlyCollection<Guid> ProductIds, IReadOnlyCollection<Guid> UserIds);

/// <summary>
/// <c>tryon-prepare</c>, every minute (<c>docs/design/try-on.md</c>, "The job"). Nothing pushes into try-on, so the job looks:
/// <list type="number">
/// <item>deletes renders made for other measurements than the user's current ones, and renders unused for <c>TryOn:RenderRetention</c>;</item>
/// <item>prepares due wardrobe items (ready, with a cutout and a try-on category, never <c>person_wearing</c>, never a catalogue copy), up to
/// <c>TryOn:WardrobeBatch</c>;</item>
/// <item>prepares due products under <see cref="TryOnCatalogueLock"/>, one at a time, <c>TryOn:DayCatalogueBatch</c> by day and
/// <c>TryOn:NightCatalogueBatch</c> at night.</item>
/// </list>
/// Due and order come from <see cref="TryOnPrepareRules.Due"/>: asked for by a request, new, source changed, retries, then the version re-prepare
/// (low priority). A run stops starting prepares after <c>TryOn:RunBudget</c>. Deliberately no <c>[DisableConcurrentExecution]</c>: every prepare
/// claims its row, and the catalogue has its own lock, so an overlapping run only does other work. Does nothing while <c>TryOn:Enabled</c> is false.
/// </summary>
internal sealed partial class TryOnPrepareJob(
    KombinDbContext db,
    TryOnPreparer preparer,
    TryOnCatalogueLock catalogueLock,
    IObjectStorage storage,
    IOptions<TryOnOptions> options,
    IClock clock,
    ILogger<TryOnPrepareJob> logger)
{
    public const string JobId = "tryon-prepare";

    public const string Cron = "* * * * *";

    /// <summary>Renders looked at per run by each of the two sweeps.</summary>
    public const int SweepBatch = 500;

    private sealed record Candidate(Guid OwnerId, string Hash, PrepareUrgency Urgency, DateTimeOffset CreatedAt);

    /// <summary>A wardrobe item or product with the hash of its current source and its garment row, if any.</summary>
    private sealed record CandidateRow(
        Guid OwnerId,
        string Hash,
        DateTimeOffset CreatedAt,
        Guid? GarmentId,
        string? Status,
        string? SourceHash,
        string? PreparedHash,
        string? PrepareVersion,
        int? Attempts,
        DateTimeOffset? NextAttemptAt,
        DateTimeOffset? LeaseUntil,
        DateTimeOffset? RequestedAt);

    [AutomaticRetry(Attempts = 0)]
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        var summary = await ProcessAsync(clock.UtcNow, null, cancellationToken);
        if (summary.RendersDeleted > 0 || summary.WardrobePrepared > 0 || summary.CataloguePrepared > 0 || summary.Failures > 0)
        {
            LogRun(logger, summary.RendersDeleted, summary.WardrobePrepared, summary.CataloguePrepared, summary.Failures);
        }
    }

    /// <summary>
    /// Fire-and-forget from a request that found pieces without an answer: the wardrobe items at once, the products only when the catalogue lock is
    /// free (otherwise the minute run takes them first: they are marked as asked for).
    /// </summary>
    [AutomaticRetry(Attempts = 0)]
    [JobDisplayName("tryon-prepare (requested)")]
    public async Task PrepareRequestedAsync(Guid[] wardrobeItemIds, Guid[] productIds, CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        var now = clock.UtcNow;
        var version = await CurrentVersionAsync(cancellationToken);
        foreach (var itemId in wardrobeItemIds)
        {
            await preparer.PrepareWardrobeItemAsync(itemId, version, now, cancellationToken);
        }

        if (productIds.Length == 0)
        {
            return;
        }

        await using var held = await catalogueLock.TryAcquireAsync(cancellationToken);
        if (held is null)
        {
            return;
        }

        foreach (var productId in productIds)
        {
            await preparer.PrepareProductAsync(productId, version, clock.UtcNow, cancellationToken);
        }
    }

    public async Task<TryOnPrepareSummary> ProcessAsync(DateTimeOffset now, TryOnScope? scope, CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();
        var settings = options.Value;
        var rendersDeleted = await SweepRendersAsync(now, scope?.UserIds, cancellationToken);
        var version = await CurrentVersionAsync(cancellationToken);
        var (wardrobe, catalogue, failures) = (0, 0, 0);

        foreach (var candidate in Order(await WardrobeCandidatesAsync(scope, version, cancellationToken), version, now).Take(settings.WardrobeBatch))
        {
            if (started.Elapsed > settings.RunBudget)
            {
                break;
            }

            try
            {
                wardrobe += await preparer.PrepareWardrobeItemAsync(candidate.OwnerId, version, now, cancellationToken) == PrepareOutcome.Prepared ? 1 : 0;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failures++;
                LogPrepareFailed(logger, exception, candidate.OwnerId);
            }
        }

        var batch = TryOnPrepareRules.IsNight(TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, Istanbul.Zone).DateTime), settings.NightStartsAt, settings.NightEndsAt)
            ? settings.NightCatalogueBatch
            : settings.DayCatalogueBatch;
        var products = batch == 0 ? [] : Order(await ProductCandidatesAsync(scope, version, cancellationToken), version, now).Take(batch).ToList();
        var busy = false;

        if (products.Count > 0)
        {
            await using var held = await catalogueLock.TryAcquireAsync(cancellationToken);
            busy = held is null;

            foreach (var candidate in held is null ? [] : products)
            {
                if (started.Elapsed > settings.RunBudget)
                {
                    break;
                }

                try
                {
                    catalogue += await preparer.PrepareProductAsync(candidate.OwnerId, version, now, cancellationToken) == PrepareOutcome.Prepared ? 1 : 0;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    failures++;
                    LogPrepareFailed(logger, exception, candidate.OwnerId);
                }
            }
        }

        return new TryOnPrepareSummary(rendersDeleted, wardrobe, catalogue, failures, busy);
    }

    /// <summary>"The current version": the latest answer's. Null before the first answer.</summary>
    public Task<string?> CurrentVersionAsync(CancellationToken cancellationToken) =>
        db.TryOnGarments.AsNoTracking()
            .Where(garment => garment.PreparedAt != null)
            .OrderByDescending(garment => garment.PreparedAt)
            .Select(garment => garment.PrepareVersion)
            .FirstOrDefaultAsync(cancellationToken);

    private static IEnumerable<Candidate> Order(List<CandidateRow> rows, string? version, DateTimeOffset now) =>
        rows.Select(row => new Candidate(row.OwnerId, row.Hash, TryOnPrepareRules.Due(StateOf(row), row.Hash, version, now), row.CreatedAt))
            .Where(candidate => candidate.Urgency != PrepareUrgency.None)
            .OrderBy(candidate => candidate.Urgency)
            .ThenBy(candidate => candidate.CreatedAt)
            .ThenBy(candidate => candidate.OwnerId);

    private static GarmentRowState? StateOf(CandidateRow row) =>
        row.GarmentId is null || !Enum.TryParse<TryOnGarmentStatus>(row.Status, out var status)
            ? null
            : new GarmentRowState(status, row.SourceHash ?? string.Empty, row.PreparedHash, row.PrepareVersion, row.Attempts ?? 0, row.NextAttemptAt, row.LeaseUntil, row.RequestedAt);

    /// <summary>
    /// Wardrobe items that may be due: ready, not archived, a cutout, a try-on category, no <c>person_wearing</c>, not a catalogue copy, and no
    /// answered row of the same source and version (a loose filter: <see cref="TryOnPrepareRules.Due"/> decides). The hash is
    /// <see cref="TryOnSources.Wardrobe"/>'s, computed by PostgreSQL.
    /// </summary>
    private Task<List<CandidateRow>> WardrobeCandidatesAsync(TryOnScope? scope, string? version, CancellationToken cancellationToken)
    {
        var slugs = TryOnFamilies.CategorySlugs.ToArray();
        var scoped = scope is not null;
        var itemIds = scope?.WardrobeItemIds.ToArray() ?? [];
        var userIds = scope?.UserIds.ToArray() ?? [];
        var current = version ?? string.Empty;

        return db.Database.SqlQuery<CandidateRow>($"""
            WITH items AS (
                SELECT i.id AS owner_id, i.created_at,
                       encode(sha256(convert_to('wardrobe|' || c.slug || '|' || i.image_cutout_key, 'UTF8')), 'hex') AS hash
                FROM wardrobe.items i
                JOIN catalog.categories c ON c.id = i.category_id
                WHERE i.status = 'Ready' AND NOT i.is_archived AND i.image_cutout_key IS NOT NULL
                  AND NOT (i.from_catalog AND i.product_id IS NOT NULL)
                  AND NOT ('person_wearing' = ANY(i.warnings))
                  AND c.slug = ANY({slugs})
                  AND (NOT {scoped} OR i.id = ANY({itemIds}) OR i.user_id = ANY({userIds}))
            )
            SELECT i.owner_id, i.hash, i.created_at, g.id AS garment_id, g.status, g.source_hash, g.prepared_hash, g.prepare_version, g.attempts,
                   g.next_attempt_at, g.lease_until, g.requested_at
            FROM items i
            LEFT JOIN tryon.garments g ON g.wardrobe_item_id = i.owner_id
            WHERE g.id IS NULL OR g.status IN ('Pending', 'Failed') OR g.source_hash <> i.hash
               OR (g.status = 'Prepared' AND (g.prepared_hash IS DISTINCT FROM g.source_hash OR g.prepare_version IS DISTINCT FROM {current}))
            """).ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Products that may be due: active, or a wardrobe item came from it, or a request asked for it; at least one image; a try-on category. The
    /// hash is <see cref="TryOnSources.Product"/>'s over the first six images in gallery order, computed by PostgreSQL.
    /// </summary>
    private Task<List<CandidateRow>> ProductCandidatesAsync(TryOnScope? scope, string? version, CancellationToken cancellationToken)
    {
        var slugs = TryOnFamilies.CategorySlugs.ToArray();
        var scoped = scope is not null;
        var productIds = scope?.ProductIds.ToArray() ?? [];
        var current = version ?? string.Empty;

        return db.Database.SqlQuery<CandidateRow>($"""
            WITH products AS (
                SELECT p.id AS owner_id, p.created_at, c.slug, (array_agg(i.url ORDER BY i.sort_order, i.id))[1:6] AS urls
                FROM catalog.products p
                JOIN catalog.categories c ON c.id = p.category_id
                JOIN catalog.product_images i ON i.product_id = p.id
                WHERE c.slug = ANY({slugs})
                  AND (p.is_active
                       OR EXISTS (SELECT 1 FROM wardrobe.items w WHERE w.product_id = p.id AND w.from_catalog AND NOT w.is_archived)
                       OR EXISTS (SELECT 1 FROM tryon.garments r WHERE r.product_id = p.id AND r.requested_at IS NOT NULL))
                  AND (NOT {scoped} OR p.id = ANY({productIds}))
                GROUP BY p.id, p.created_at, c.slug
            ),
            hashed AS (
                SELECT owner_id, created_at, encode(sha256(convert_to('product|' || slug || '|' || array_to_string(urls, E'\n'), 'UTF8')), 'hex') AS hash
                FROM products
            )
            SELECT h.owner_id, h.hash, h.created_at, g.id AS garment_id, g.status, g.source_hash, g.prepared_hash, g.prepare_version, g.attempts,
                   g.next_attempt_at, g.lease_until, g.requested_at
            FROM hashed h
            LEFT JOIN tryon.garments g ON g.product_id = h.owner_id
            WHERE g.id IS NULL OR g.status IN ('Pending', 'Failed') OR g.source_hash <> h.hash
               OR (g.status = 'Prepared' AND (g.prepared_hash IS DISTINCT FROM g.source_hash OR g.prepare_version IS DISTINCT FROM {current}))
            """).ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Deletes renders that can never be served again and show proportions the user no longer has (made before the profile's last change, for
    /// another body), and renders unused for <c>TryOn:RenderRetention</c>: objects first, then rows.
    /// </summary>
    private async Task<int> SweepRendersAsync(DateTimeOffset now, IReadOnlyCollection<Guid>? userIds, CancellationToken cancellationToken)
    {
        var changed = from render in db.TryOnRenders
                      join profile in db.UserProfiles on render.UserId equals profile.UserId
                      where profile.UpdatedAt > render.CreatedAt
                      orderby profile.UpdatedAt descending
                      select new { render.Id, render.UserId, render.BodyHash, render.ImageKey, Profile = profile };
        var cutoff = now - options.Value.RenderRetention;
        var unused = db.TryOnRenders.Where(render => render.LastUsedAt < cutoff);

        if (userIds is not null)
        {
            changed = changed.Where(render => userIds.Contains(render.UserId));
            unused = unused.Where(render => userIds.Contains(render.UserId));
        }

        var stale = (await changed.AsNoTracking().Take(SweepBatch).ToListAsync(cancellationToken))
            .Where(render => render.BodyHash != TryOnBody.From(render.Profile).Hash())
            .Select(render => (render.Id, ImageKey: (string?)render.ImageKey))
            .ToList();
        var expired = await unused.OrderBy(render => render.LastUsedAt).Take(SweepBatch).Select(render => new { render.Id, render.ImageKey }).ToListAsync(cancellationToken);

        var doomed = stale.Concat(expired.Select(render => (render.Id, ImageKey: (string?)render.ImageKey))).DistinctBy(render => render.Id).ToList();
        if (doomed.Count == 0)
        {
            return 0;
        }

        await storage.DeleteAsync([.. doomed.Select(render => render.ImageKey).OfType<string>()], cancellationToken);
        var ids = doomed.Select(render => render.Id).ToList();

        return await db.TryOnRenders.Where(render => ids.Contains(render.Id)).ExecuteDeleteAsync(cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "tryon-prepare: {Renders} render(s) deleted, {Wardrobe} wardrobe and {Catalogue} catalogue garment(s) prepared, {Failures} failure(s).")]
    private static partial void LogRun(ILogger logger, int renders, int wardrobe, int catalogue, int failures);

    [LoggerMessage(Level = LogLevel.Error, Message = "tryon-prepare: preparing {OwnerId} failed.")]
    private static partial void LogPrepareFailed(ILogger logger, Exception exception, Guid ownerId);
}
