using Kombin.Api.Common.Time;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Profile;

namespace Kombin.Api.Features.Profile;

/// <summary>A persona and its style profile, loaded once per request so catalog endpoints can score many products with it.</summary>
internal sealed class PersonaScoring(Persona persona, PersonaStyleProfile styleProfile)
{
    public int Score(ProductTraits product) => MatchScorer.Score(styleProfile, persona, product);

    public int Score(Product product) => MatchScorer.Score(styleProfile, persona, product);

    /// <summary>Null when the persona does not exist, is archived or belongs to someone else.</summary>
    public static async Task<PersonaScoring?> LoadAsync(
        KombinDbContext db, IClock clock, Guid userId, Guid personaId, CancellationToken cancellationToken)
    {
        var persona = await db.Personas.AsNoTracking().OwnedBy(userId).SingleOrDefaultAsync(candidate => candidate.Id == personaId, cancellationToken);

        return persona is null
            ? null
            : new PersonaScoring(persona, await StyleProfiles.LoadOrDeriveAsync(db, persona, clock.UtcNow, cancellationToken));
    }
}
