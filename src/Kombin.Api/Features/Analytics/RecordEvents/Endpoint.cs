using System.Text.Json;
using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Time;
using Kombin.Api.Common.Validation;
using Kombin.Api.Features.Catalog;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Analytics;

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

internal static class RecordEventsEndpoint
{
    /// <summary>The event repeats one earlier in the same batch with the same session, name and time.</summary>
    public const string Duplicate = "duplicate";

    /// <summary>An anonymous caller sent the event without an <c>anonymousId</c>, so it could never be attributed.</summary>
    public const string MissingIdentity = "missing_identity";

    public static void Map(RouteGroupBuilder group) =>
        group.MapPost("/", Handle)
            .WithName("RecordEvents")
            .WithValidation<RecordEventsRequest>()
            .Produces<RecordEventsResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest);

    /// <summary>
    /// Public: the app reports before login. A valid bearer token adds the user to every event. A bad clock is clamped, not rejected;
    /// what is not stored (a repeat within the batch, an event nobody could be blamed for) is listed in <c>rejected</c> with its index.
    /// </summary>
    private static async Task<IResult> Handle(RecordEventsRequest request, ICurrentUser currentUser, KombinDbContext db, IClock clock, CancellationToken cancellationToken)
    {
        var receivedAt = clock.UtcNow;
        var userId = currentUser.UserIdOrNull;

        var stored = new List<AnalyticsEvent>(request.Events.Count);
        var rejected = new List<RejectedEvent>();
        var seen = new HashSet<(string SessionId, string Name, DateTimeOffset OccurredAt)>();

        for (var index = 0; index < request.Events.Count; index++)
        {
            var item = request.Events[index];

            if (userId is null && item.AnonymousId is null)
            {
                rejected.Add(new RejectedEvent(index, MissingIdentity));
                continue;
            }

            // The client's own time is the key, before clamping: two different moments must not collapse into one just because both were clamped.
            if (!seen.Add((item.SessionId, item.Name, item.OccurredAt!.Value)))
            {
                rejected.Add(new RejectedEvent(index, Duplicate));
                continue;
            }

            _ = EnumNames.TryParse<AnalyticsPlatform>(item.Platform, out var platform);

            stored.Add(new AnalyticsEvent
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
            });
        }

        if (stored.Count > 0)
        {
            db.AnalyticsEvents.AddRange(stored);
            await db.SaveChangesAsync(cancellationToken);
        }

        return Results.Json(new RecordEventsResponse(stored.Count, rejected), statusCode: StatusCodes.Status202Accepted);
    }
}
