using System.Text.Json;
using Hangfire;
using Kombin.Api.Common.Time;
using Kombin.Api.Persistence;
using NotificationEntity = Kombin.Api.Persistence.Entities.Notification.Notification;

namespace Kombin.Api.Features.Notification.Jobs;

internal readonly record struct DispatchSummary(int Created, int Skipped)
{
    public int Consumed => Created + Skipped;
}

/// <summary>
/// Turns unconsumed alert triggers (<c>engagement.alert_triggers</c>) into notifications. Idempotent: a trigger is consumed in the
/// same transaction that creates its notification, so a rerun finds nothing to do, and the 24-hour dedupe keeps a burst of
/// triggers for one product down to one notification. The rules themselves are <see cref="DispatchRules"/>.
/// </summary>
internal sealed partial class AlertDispatcherJob(KombinDbContext db, IClock clock, ILogger<AlertDispatcherJob> logger)
{
    public const string JobId = "alert-dispatcher";

    public const int BatchSize = 500;

    [AutomaticRetry(Attempts = 0)]
    [DisableConcurrentExecution(60)]
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var summary = await DispatchAsync(null, cancellationToken);

        if (summary.Consumed > 0)
        {
            LogDispatched(logger, summary.Created, summary.Skipped);
        }
    }

    /// <summary>Dispatches up to <see cref="BatchSize"/> triggers, oldest first; <paramref name="userId"/> restricts it to one user (tests, support).</summary>
    public async Task<DispatchSummary> DispatchAsync(Guid? userId, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;

        var pending = db.AlertTriggers.Where(trigger => trigger.ConsumedAt == null);
        if (userId is { } onlyUser)
        {
            pending = pending.Where(trigger => trigger.UserId == onlyUser);
        }

        var triggers = await pending
            .OrderBy(trigger => trigger.TriggeredAt)
            .ThenBy(trigger => trigger.Id)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        if (triggers.Count == 0)
        {
            return default;
        }

        var userIds = triggers.Select(trigger => trigger.UserId).Distinct().ToList();
        var productIds = triggers.Select(trigger => trigger.ProductId).Distinct().ToList();

        var preferences = await db.NotificationPreferences
            .AsNoTracking()
            .Where(preference => userIds.Contains(preference.UserId))
            .ToDictionaryAsync(preference => preference.UserId, cancellationToken);

        var usersWithDevice = (await db.Devices
            .AsNoTracking()
            .Where(device => device.IsActive && userIds.Contains(device.UserId))
            .Select(device => device.UserId)
            .Distinct()
            .ToListAsync(cancellationToken)).ToHashSet();

        var products = await db.Products
            .AsNoTracking()
            .Where(product => productIds.Contains(product.Id))
            .Select(product => new { product.Id, product.Name, BrandName = product.Brand.Name, CategoryName = product.Category.Name })
            .ToDictionaryAsync(product => product.Id, cancellationToken);

        var dedupeKeys = triggers
            .Select(trigger => NotificationKinds.AlertDedupeKey(NotificationKinds.From(trigger.Kind), trigger.ProductId))
            .Distinct()
            .ToList();
        var since = now - DispatchRules.DedupeWindow;

        var recent = (await db.Notifications
            .AsNoTracking()
            .Where(notification => userIds.Contains(notification.UserId) && dedupeKeys.Contains(notification.DedupeKey) && notification.CreatedAt > since)
            .Select(notification => new { notification.UserId, notification.DedupeKey })
            .ToListAsync(cancellationToken)).Select(row => (row.UserId, row.DedupeKey)).ToHashSet();

        var created = 0;
        var skipped = 0;

        foreach (var trigger in triggers)
        {
            var kind = NotificationKinds.From(trigger.Kind);
            var dedupeKey = NotificationKinds.AlertDedupeKey(kind, trigger.ProductId);

            var decision = DispatchRules.Decide(
                kind,
                preferences.GetValueOrDefault(trigger.UserId),
                usersWithDevice.Contains(trigger.UserId),
                recent.Contains((trigger.UserId, dedupeKey)),
                now,
                QuietHours.Zone);

            trigger.ConsumedAt = now;

            if (!decision.Create)
            {
                skipped++;
                continue;
            }

            var product = products[trigger.ProductId];
            var text = Copy.ForAlert(
                trigger.Kind,
                new AlertCopyInput(product.CategoryName, product.BrandName, product.Name, trigger.PriceBefore, trigger.PriceAfter, ReadSizes(trigger.Payload)));

            db.Notifications.Add(new NotificationEntity
            {
                UserId = trigger.UserId,
                Kind = kind,
                Title = text.Title,
                Body = text.Body,
                Data = NotificationData.ForAlert(trigger.ProductId, trigger.OfferId, trigger.PriceAlertId),
                DedupeKey = dedupeKey,
                CreatedAt = now,
                ScheduledFor = decision.ScheduledFor,
                Status = decision.Status,
                FailureReason = decision.FailureReason,
                AlertTriggerId = trigger.Id,
            });

            // A later trigger of the same batch for the same product is a repeat too.
            recent.Add((trigger.UserId, dedupeKey));
            created++;
        }

        await db.SaveChangesAsync(cancellationToken);

        return new DispatchSummary(created, skipped);
    }

    private static string[] ReadSizes(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);

            return document.RootElement.TryGetProperty("sizes", out var sizes) && sizes.ValueKind == JsonValueKind.Array
                ? [.. sizes.EnumerateArray().Where(size => size.ValueKind == JsonValueKind.String).Select(size => size.GetString()!)]
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Alert dispatcher: {Created} notification(s) created, {Skipped} trigger(s) skipped.")]
    private static partial void LogDispatched(ILogger logger, int created, int skipped);
}
