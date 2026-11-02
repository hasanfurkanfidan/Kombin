using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Http;
using Kombin.Api.Common.Results;
using Kombin.Api.Common.Time;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Wardrobe;
using Npgsql;

namespace Kombin.Api.Features.Wardrobe.RecordWear;

/// <summary><c>WornOn</c> defaults to today in Europe/Istanbul. The body may be left out entirely.</summary>
internal sealed record RecordWearRequest(DateOnly? WornOn);

/// <summary><c>WornOn</c> is the day just recorded, or null after an undo.</summary>
internal sealed record WearResponse(Guid ItemId, DateOnly? WornOn, int WearCount, DateOnly? LastWornAt);

internal static class RecordWearEndpoint
{
    public static void Map(RouteGroupBuilder group) =>
        group.MapPost("/items/{id:guid}/wear", Handle)
            .WithName("RecordWardrobeWear")
            .Produces<WearResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

    /// <summary>Marks the item worn on a day. Marking the same day again changes nothing (one row per item and day).</summary>
    private static async Task<IResult> Handle(
        Guid id,
        RecordWearRequest? request,
        ICurrentUser currentUser,
        KombinDbContext db,
        IClock clock,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var item = await db.WardrobeItems.SingleOrDefaultAsync(candidate => candidate.Id == id && candidate.UserId == currentUser.UserId, cancellationToken);
        if (item is null)
        {
            return WardrobeErrors.ItemNotFound.ToHttpResult();
        }

        if (item.IsArchived)
        {
            return WardrobeErrors.ItemArchived.ToHttpResult();
        }

        var now = clock.UtcNow;
        var (day, error) = WearRules.Resolve(request?.WornOn, WearRules.Today(now));
        if (error is not null)
        {
            return Problems.Validation(httpContext, new Dictionary<string, string[]> { ["wornOn"] = [error] });
        }

        if (!await db.WardrobeWears.AnyAsync(wear => wear.ItemId == item.Id && wear.WornOn == day, cancellationToken))
        {
            db.WardrobeWears.Add(new WardrobeWear { ItemId = item.Id, UserId = item.UserId, WornOn = day, CreatedAt = now });

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                // A parallel request recorded the same day; the outcome is the same.
                db.ChangeTracker.Entries<WardrobeWear>().ToList().ForEach(entry => entry.State = EntityState.Detached);
            }
        }

        var summary = await WearSummaries.RefreshAsync(db, item, cancellationToken);

        return TypedResults.Ok(new WearResponse(item.Id, day, summary.WearCount, summary.LastWornAt));
    }
}

internal static class WearSummaries
{
    /// <summary>Recomputes <c>WearCount</c> and <c>LastWornAt</c> of the tracked <paramref name="item"/> from its wear rows and saves.</summary>
    public static async Task<(int WearCount, DateOnly? LastWornAt)> RefreshAsync(KombinDbContext db, WardrobeItem item, CancellationToken cancellationToken)
    {
        var days = await db.WardrobeWears.Where(wear => wear.ItemId == item.Id).Select(wear => wear.WornOn).ToListAsync(cancellationToken);
        var summary = WearRules.Summarize(days);

        item.WearCount = summary.WearCount;
        item.LastWornAt = summary.LastWornAt;
        await db.SaveChangesAsync(cancellationToken);

        return summary;
    }
}
