using Kombin.Api.Common.Auth;
using Kombin.Api.Persistence;

namespace Kombin.Api.Features.Engagement.ListSavedIds;

internal sealed record SavedIdsResponse(IReadOnlyList<Guid> ProductIds);

internal static class ListSavedIdsEndpoint
{
    /// <summary>Plenty for bookmark states in the app; a user with more simply sees the newest ones.</summary>
    public const int MaxIds = 1000;

    public static void Map(RouteGroupBuilder group) =>
        group.MapGet("/ids", Handle)
            .WithName("ListSavedIds")
            .Produces<SavedIdsResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

    private static async Task<IResult> Handle(ICurrentUser currentUser, KombinDbContext db, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId;

        var ids = await db.SavedProducts
            .AsNoTracking()
            .Where(saved => saved.UserId == userId)
            .OrderByDescending(saved => saved.CreatedAt)
            .ThenByDescending(saved => saved.ProductId)
            .Take(MaxIds)
            .Select(saved => saved.ProductId)
            .ToListAsync(cancellationToken);

        return Results.Ok(new SavedIdsResponse(ids));
    }
}
