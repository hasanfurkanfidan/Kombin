using Hangfire;
using Kombin.Api.Common.Time;
using Kombin.Api.Features.Profile;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Notification;
using NotificationEntity = Kombin.Api.Persistence.Entities.Notification.Notification;

namespace Kombin.Api.Features.Notification.Jobs;

/// <summary>
/// Sunday evening: for each user with a device and recommendations on, tells them about the best new products for their active persona.
/// Idempotent through the dedupe key <c>newmatches:{personaId}:{isoWeek}</c>, which also holds across runs a day apart.
/// </summary>
internal sealed partial class WeeklyMatchesJob(KombinDbContext db, IClock clock, ILogger<WeeklyMatchesJob> logger)
{
    public const string JobId = "weekly-matches";

    /// <summary>Sunday 19:00 in <see cref="QuietHours.ZoneId"/>.</summary>
    public const string Cron = "0 19 * * 0";

    /// <summary>Only the newest products are scored, as in <c>sort=match</c> of the catalog list.</summary>
    public const int CandidateLimit = 500;

    private const int UsersPerPage = 200;

    private static readonly TimeSpan Lookback = TimeSpan.FromDays(7);

    [AutomaticRetry(Attempts = 0)]
    [DisableConcurrentExecution(60)]
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var created = await SendAsync(null, null, cancellationToken);
        LogRun(logger, created);
    }

    /// <summary>
    /// Returns the number of notifications created. <paramref name="userId"/> restricts the run to one user (tests, support) and
    /// <paramref name="products"/> the candidates to those products (tests: the candidates are otherwise every new product of the shared database).
    /// </summary>
    public async Task<int> SendAsync(Guid? userId, IReadOnlyCollection<Guid>? products, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var since = now - Lookback;

        var candidates = await LoadCandidatesAsync(since, products, cancellationToken);
        if (candidates.Count == 0)
        {
            return 0;
        }

        var isoWeek = WeeklyMatchSelection.IsoWeekKey(now, QuietHours.Zone);
        var created = 0;
        Guid? last = null;

        while (true)
        {
            var users = db.Devices.AsNoTracking().Where(device => device.IsActive);
            if (userId is { } onlyUser)
            {
                users = users.Where(device => device.UserId == onlyUser);
            }

            if (last is { } after)
            {
                users = users.Where(device => device.UserId > after);
            }

            var page = await users.Select(device => device.UserId).Distinct().OrderBy(id => id).Take(UsersPerPage).ToListAsync(cancellationToken);
            if (page.Count == 0)
            {
                return created;
            }

            last = page[^1];
            created += await SendPageAsync(page, candidates, isoWeek, now, since, cancellationToken);
        }
    }

    private async Task<List<WeeklyCandidate>> LoadCandidatesAsync(DateTimeOffset since, IReadOnlyCollection<Guid>? products, CancellationToken cancellationToken)
    {
        var newProducts = db.Products.AsNoTracking().Where(product => product.IsActive && product.CreatedAt >= since);
        if (products is not null)
        {
            newProducts = newProducts.Where(product => products.Contains(product.Id));
        }

        var rows = await newProducts
            .Where(product => product.Offers.Any(offer => offer.IsActive && (offer.StockStatus == StockStatus.InStock || offer.StockStatus == StockStatus.LowStock)))
            .OrderByDescending(product => product.CreatedAt)
            .ThenByDescending(product => product.Id)
            .Take(CandidateLimit)
            .Select(product => new
            {
                product.Id,
                BrandName = product.Brand.Name,
                product.Name,
                product.Audience,
                product.PrimaryColor,
                product.Formality,
                product.Fit,
                product.Seasons,
                product.Pattern,
                CategorySlug = product.Category.Slug,
            })
            .ToListAsync(cancellationToken);

        return
        [
            .. rows.Select(row => new WeeklyCandidate(
                row.Id,
                Copy.ProductDisplayName(row.BrandName, row.Name),
                row.Audience,
                new ProductTraits(row.PrimaryColor, row.Formality, row.Fit, row.Seasons, row.Pattern, row.CategorySlug))),
        ];
    }

    private async Task<int> SendPageAsync(
        List<Guid> userIds, List<WeeklyCandidate> candidates, string isoWeek, DateTimeOffset now, DateTimeOffset since, CancellationToken cancellationToken)
    {
        var preferences = await db.NotificationPreferences.AsNoTracking()
            .Where(preference => userIds.Contains(preference.UserId))
            .ToDictionaryAsync(preference => preference.UserId, cancellationToken);

        var profiles = await db.UserProfiles.AsNoTracking()
            .Where(profile => userIds.Contains(profile.UserId))
            .ToDictionaryAsync(profile => profile.UserId, cancellationToken);

        var personas = (await db.Personas.AsNoTracking()
            .Where(persona => userIds.Contains(persona.UserId) && !persona.IsArchived)
            .OrderBy(persona => persona.SortOrder)
            .ThenBy(persona => persona.CreatedAt)
            .ToListAsync(cancellationToken)).ToLookup(persona => persona.UserId);

        // Pending counts as sent: the sender may not have run yet, and the cap is about what the user is going to receive.
        var sentThisWeek = (await db.Notifications.AsNoTracking()
            .Where(notification => userIds.Contains(notification.UserId)
                && notification.Kind == NotificationKind.NewMatches
                && notification.CreatedAt >= since
                && (notification.Status == NotificationStatus.Sent || notification.Status == NotificationStatus.Pending))
            .GroupBy(notification => notification.UserId)
            .Select(group => new { UserId = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken)).ToDictionary(row => row.UserId, row => row.Count);

        var weekKeys = (await db.Notifications.AsNoTracking()
            .Where(notification => userIds.Contains(notification.UserId) && notification.Kind == NotificationKind.NewMatches && notification.DedupeKey.EndsWith(isoWeek))
            .Select(notification => notification.DedupeKey)
            .ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);

        var created = 0;

        foreach (var userId in userIds)
        {
            var preference = preferences.GetValueOrDefault(userId);
            var limit = preference?.MaxRecommendationsPerWeek ?? NotificationPreference.DefaultMaxRecommendationsPerWeek;

            if (!NotificationKinds.IsEnabled(preference, NotificationKind.NewMatches) || sentThisWeek.GetValueOrDefault(userId) >= limit)
            {
                continue;
            }

            var userPersonas = personas[userId].ToList();
            var persona = userPersonas.FirstOrDefault(candidate => candidate.Id == profiles.GetValueOrDefault(userId)?.ActivePersonaId) ?? userPersonas.FirstOrDefault();
            if (persona is null)
            {
                continue;
            }

            var dedupeKey = WeeklyMatchSelection.DedupeKey(persona.Id, isoWeek);
            if (weekKeys.Contains(dedupeKey))
            {
                continue;
            }

            var styleProfile = await StyleProfiles.LoadOrDeriveAsync(db, persona, now, cancellationToken);
            var picks = WeeklyMatchSelection.Select(
                candidates,
                profiles.GetValueOrDefault(userId)?.Audience ?? Audience.Unisex,
                traits => MatchScorer.Score(styleProfile, persona, traits));

            if (picks.Count == 0)
            {
                continue;
            }

            var text = Copy.ForNewMatches(persona.Name, picks.Count, picks.Min(pick => pick.Score), picks.Select(pick => pick.Candidate.DisplayName));

            db.Notifications.Add(new NotificationEntity
            {
                UserId = userId,
                Kind = NotificationKind.NewMatches,
                Title = text.Title,
                Body = text.Body,
                Data = NotificationData.ForNewMatches(persona.Id, [.. picks.Select(pick => pick.Candidate.ProductId)]),
                DedupeKey = dedupeKey,
                CreatedAt = now,
                ScheduledFor = QuietHours.NextAllowed(now, preference?.QuietHoursStart, preference?.QuietHoursEnd, QuietHours.Zone),
                Status = NotificationStatus.Pending,
            });

            weekKeys.Add(dedupeKey);
            created++;
        }

        // One save per page of users: a crash loses at most a page, and the dedupe key makes the rerun skip what was saved.
        await db.SaveChangesAsync(cancellationToken);

        return created;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Weekly matches: {Count} notification(s) created.")]
    private static partial void LogRun(ILogger logger, int count);
}
