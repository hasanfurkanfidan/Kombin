using System.Text.Json;
using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Pagination;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Engagement;

namespace Kombin.Api.Features.Engagement.ListTriggers;

/// <summary><c>AlertId</c> is null once the alert has been deleted.</summary>
internal sealed record TriggerResponse(
    long Id,
    Guid? AlertId,
    AlertKind Kind,
    ProductSummary Product,
    Guid OfferId,
    decimal? PriceBefore,
    decimal? PriceAfter,
    JsonElement Payload,
    DateTimeOffset TriggeredAt,
    DateTimeOffset? ConsumedAt);

internal static class ListTriggersEndpoint
{
    public static void Map(RouteGroupBuilder group) =>
        group.MapGet("/triggers", Handle)
            .WithName("ListAlertTriggers")
            .Produces<PagedResponse<TriggerResponse>>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

    private static async Task<IResult> Handle([AsParameters] PagedRequest paging, ICurrentUser currentUser, KombinDbContext db, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId;
        var triggers = db.AlertTriggers.AsNoTracking().Where(trigger => trigger.UserId == userId);

        var totalCount = await triggers.CountAsync(cancellationToken);
        var page = await triggers
            .OrderByDescending(trigger => trigger.TriggeredAt)
            .ThenByDescending(trigger => trigger.Id)
            .Skip(paging.Skip)
            .Take(paging.EffectivePageSize)
            .ToListAsync(cancellationToken);

        var cards = await ProductSummaries.LoadAsync(db, [.. page.Select(trigger => trigger.ProductId).Distinct()], cancellationToken);

        var items = page
            .Select(trigger => new TriggerResponse(
                trigger.Id,
                trigger.PriceAlertId,
                trigger.Kind,
                cards[trigger.ProductId].ToSummary(),
                trigger.OfferId,
                trigger.PriceBefore,
                trigger.PriceAfter,
                JsonDocument.Parse(trigger.Payload).RootElement.Clone(),
                trigger.TriggeredAt,
                trigger.ConsumedAt))
            .ToList();

        return Results.Ok(PagedResponse<TriggerResponse>.Create(items, paging, totalCount));
    }
}
