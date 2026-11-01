using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Profile;

namespace Kombin.Api.Features.Profile;

/// <summary>Reading, deriving and storing <see cref="PersonaStyleProfile"/>s; the arithmetic itself is in <see cref="StyleProfileBuilder"/>.</summary>
internal static class StyleProfiles
{
    public static async Task<List<StyleSwipeSample>> LoadSamplesAsync(KombinDbContext db, Guid personaId, CancellationToken cancellationToken) =>
        await db.StyleSwipes
            .AsNoTracking()
            .Where(swipe => swipe.PersonaId == personaId)
            .Join(db.StyleCards, swipe => swipe.StyleCardId, card => card.Id, (swipe, card) => new StyleSwipeSample(swipe.Decision, card))
            .ToListAsync(cancellationToken);

    public static void Apply(PersonaStyleProfile target, StyleProfileData data, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(data);

        target.ColorWeights = data.ColorWeights;
        target.FamilyWeights = data.FamilyWeights;
        target.FitWeights = data.FitWeights;
        target.FormalityMean = data.FormalityMean;
        target.SeasonWeights = data.SeasonWeights;
        target.LikeCount = data.LikeCount;
        target.DislikeCount = data.DislikeCount;
        target.Confidence = data.Confidence;
        target.UpdatedAt = now;
    }

    /// <summary>
    /// The stored profile of the persona. A persona created by this API always has one; if it is missing anyway the profile is
    /// derived from the persona's swipes and preferences on the spot (and not stored), so readers never have to handle a gap.
    /// </summary>
    public static async Task<PersonaStyleProfile> LoadOrDeriveAsync(
        KombinDbContext db, Persona persona, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var stored = await db.PersonaStyleProfiles.AsNoTracking().SingleOrDefaultAsync(profile => profile.PersonaId == persona.Id, cancellationToken);

        if (stored is not null)
        {
            return stored;
        }

        var derived = new PersonaStyleProfile { PersonaId = persona.Id };
        Apply(derived, StyleProfileBuilder.Build(await LoadSamplesAsync(db, persona.Id, cancellationToken), persona), now);

        return derived;
    }
}
