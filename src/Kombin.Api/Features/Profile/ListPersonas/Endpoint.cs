using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Time;
using Kombin.Api.Persistence;

namespace Kombin.Api.Features.Profile.ListPersonas;

internal sealed record PersonaListResponse(IReadOnlyList<PersonaResponse> Items);

internal static class ListPersonasEndpoint
{
    public static void Map(RouteGroupBuilder group) =>
        group.MapGet("/", Handle)
            .WithName("ListPersonas")
            .Produces<PersonaListResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

    private static async Task<IResult> Handle(ICurrentUser currentUser, KombinDbContext db, IClock clock, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId;

        var personas = await db.Personas
            .AsNoTracking()
            .OwnedBy(userId)
            .OrderBy(persona => persona.SortOrder)
            .ThenBy(persona => persona.CreatedAt)
            .ToListAsync(cancellationToken);

        var personaIds = personas.Select(persona => persona.Id).ToList();
        var styleProfiles = await db.PersonaStyleProfiles
            .AsNoTracking()
            .Where(profile => personaIds.Contains(profile.PersonaId))
            .ToDictionaryAsync(profile => profile.PersonaId, cancellationToken);

        var activeId = await db.UserProfiles
            .AsNoTracking()
            .Where(profile => profile.UserId == userId)
            .Select(profile => profile.ActivePersonaId)
            .SingleOrDefaultAsync(cancellationToken);

        var items = new List<PersonaResponse>(personas.Count);

        foreach (var persona in personas)
        {
            var styleProfile = styleProfiles.TryGetValue(persona.Id, out var stored)
                ? stored
                : await StyleProfiles.LoadOrDeriveAsync(db, persona, clock.UtcNow, cancellationToken);

            items.Add(PersonaResponse.From(persona, styleProfile, persona.Id == activeId));
        }

        return Results.Ok(new PersonaListResponse(items));
    }
}
