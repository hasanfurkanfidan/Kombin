using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Results;
using Kombin.Api.Persistence;

namespace Kombin.Api.Features.Profile.ActivatePersona;

internal static class ActivatePersonaEndpoint
{
    public static void Map(RouteGroupBuilder group) =>
        group.MapPut("/{id:guid}/activate", Handle)
            .WithName("ActivatePersona")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

    private static async Task<IResult> Handle(Guid id, ICurrentUser currentUser, KombinDbContext db, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId;

        if (!await db.Personas.OwnedBy(userId).AnyAsync(persona => persona.Id == id, cancellationToken))
        {
            return ProfileErrors.PersonaNotFound.ToHttpResult();
        }

        var profile = await UserProfiles.GetOrCreateAsync(db, userId, cancellationToken);
        profile.ActivePersonaId = id;
        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.NoContent();
    }
}
