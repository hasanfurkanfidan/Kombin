using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Results;
using Kombin.Api.Persistence;

namespace Kombin.Api.Features.Profile.ArchivePersona;

internal static class ArchivePersonaEndpoint
{
    /// <summary>
    /// A soft delete: the persona disappears from the lists but its swipes stay, and its name becomes available again.
    /// If it was the active persona, the first remaining one takes over, or there is none.
    /// </summary>
    public static void Map(RouteGroupBuilder group) =>
        group.MapDelete("/{id:guid}", Handle)
            .WithName("ArchivePersona")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

    private static async Task<IResult> Handle(Guid id, ICurrentUser currentUser, KombinDbContext db, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId;

        var persona = await db.Personas.OwnedBy(userId).SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (persona is null)
        {
            return ProfileErrors.PersonaNotFound.ToHttpResult();
        }

        var profile = await UserProfiles.GetOrCreateAsync(db, userId, cancellationToken);

        persona.IsArchived = true;

        if (profile.ActivePersonaId == id)
        {
            profile.ActivePersonaId = await db.Personas
                .OwnedBy(userId)
                .Where(other => other.Id != id)
                .OrderBy(other => other.SortOrder)
                .ThenBy(other => other.CreatedAt)
                .Select(other => (Guid?)other.Id)
                .FirstOrDefaultAsync(cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.NoContent();
    }
}
