using System.Text.Json;
using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Time;
using Kombin.Api.Common.Validation;
using Kombin.Api.Features.Catalog;
using Kombin.Api.Features.Notification;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Analytics;
using Microsoft.Extensions.Options;

namespace Kombin.Api.Features.Analytics.RecordEvents;

/// <summary>
/// One event as the app sends it. <c>AnonymousId</c> is the device's pre-login ID; it is required while the caller is anonymous and
/// recommended after login. <c>Properties</c> is a flat object whose keys depend on <c>Name</c> (<see cref="AnalyticsEventCatalog"/>).
/// </summary>
internal sealed record EventItem(
    string Name,
    Dictionary<string, JsonElement>? Properties,
    string SessionId,
    string Platform,
    string AppVersion,
    DateTimeOffset? OccurredAt,
    string? AnonymousId);

internal sealed record RecordEventsRequest(List<EventItem> Events);

internal sealed record RejectedEvent(int Index, string Reason);

internal sealed record RecordEventsResponse(int Accepted, IReadOnlyList<RejectedEvent> Rejected);

internal static partial class RecordEventsEndpoint
{
    public static void Map(RouteGroupBuilder group) =>
        group.MapPost("/", Handle)
            .WithName("RecordEvents")
            .WithValidation<RecordEventsRequest>()
            .Produces<RecordEventsResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest);

    /// <summary>
    /// Public: the app reports before login. A valid bearer token adds the user to every event. A bad clock is clamped, not rejected.
    /// Events are judged one by one: what is not stored (an unknown name, bad properties, no identity, a repeat within the batch, or an actor
    /// over its daily cap) is listed in <c>rejected</c> with its index and a <see cref="RejectionReasons"/> value, and the rest of the batch is
    /// stored. Only a malformed body is a 400.
    /// </summary>
    private static async Task<IResult> Handle(
        RecordEventsRequest request,
        ICurrentUser currentUser,
        KombinDbContext db,
        IClock clock,
        IOptions<AnalyticsOptions> options,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var receivedAt = clock.UtcNow;
        var userId = currentUser.UserIdOrNull;

        var candidates = new List<(int Index, AnalyticsEvent Event)>(request.Events.Count);
        var rejected = new List<RejectedEvent>();
        var seen = new HashSet<(string SessionId, string Name, DateTimeOffset OccurredAt)>();

        for (var index = 0; index < request.Events.Count; index++)
        {
            var item = request.Events[index];

            if (AnalyticsEventCatalog.Reject(item.Name, item.Properties) is { } reason)
            {
                rejected.Add(new RejectedEvent(index, reason));
                continue;
            }

            if (userId is null && item.AnonymousId is null)
            {
                rejected.Add(new RejectedEvent(index, RejectionReasons.MissingIdentity));
                continue;
            }

            // The client's own time is the key, before clamping: two different moments must not collapse into one just because both were clamped.
            if (!seen.Add((item.SessionId, item.Name, item.OccurredAt!.Value)))
            {
                rejected.Add(new RejectedEvent(index, RejectionReasons.Duplicate));
                continue;
            }

            _ = EnumNames.TryParse<AnalyticsPlatform>(item.Platform, out var platform);

            candidates.Add((index, new AnalyticsEvent
            {
                UserId = userId,
                AnonymousId = item.AnonymousId,
                Name = item.Name,
                Properties = AnalyticsEventCatalog.Serialize(item.Properties),
                SessionId = item.SessionId,
                Platform = platform,
                AppVersion = item.AppVersion,
                OccurredAt = EventTiming.Clamp(item.OccurredAt.Value, receivedAt),
                ReceivedAt = receivedAt,
            }));
        }

        // The daily cap per actor (security review M6): the signed-in user, else the device's anonymous ID. What is over it is dropped.
        var stored = new List<AnalyticsEvent>(candidates.Count);
        var dayStart = EventCap.DayStart(receivedAt, QuietHours.Zone);
        var cap = options.Value.DailyEventCapPerActor;

        foreach (var actor in candidates.GroupBy(candidate => candidate.Event.UserId is { } user ? $"u:{user:N}" : $"a:{candidate.Event.AnonymousId}"))
        {
            var first = actor.First().Event;
            var storedToday = first.UserId is { } actorUser
                ? await db.AnalyticsEvents.CountAsync(row => row.UserId == actorUser && row.ReceivedAt >= dayStart, cancellationToken)
                : await db.AnalyticsEvents.CountAsync(row => row.UserId == null && row.AnonymousId == first.AnonymousId && row.ReceivedAt >= dayStart, cancellationToken);
            var remaining = EventCap.Remaining(storedToday, cap);

            stored.AddRange(actor.Take(remaining).Select(candidate => candidate.Event));
            var over = actor.Skip(remaining).ToList();
            if (over.Count > 0)
            {
                rejected.AddRange(over.Select(candidate => new RejectedEvent(candidate.Index, RejectionReasons.DailyCap)));
                AnalyticsMetrics.EventsCapped.Add(over.Count);
                LogCapped(loggers.CreateLogger(typeof(RecordEventsEndpoint)), over.Count, first.UserId is null ? "anonymous" : "user", cap);
            }
        }

        if (stored.Count > 0)
        {
            db.AnalyticsEvents.AddRange(stored);
            await db.SaveChangesAsync(cancellationToken);
        }

        return Results.Json(new RecordEventsResponse(stored.Count, [.. rejected.OrderBy(entry => entry.Index)]), statusCode: StatusCodes.Status202Accepted);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Analytics: {Count} event(s) of one {ActorKind} actor dropped by the daily cap of {Cap}.")]
    private static partial void LogCapped(ILogger logger, int count, string actorKind, int cap);
}
