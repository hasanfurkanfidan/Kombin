using Kombin.Api.Common.Results;
using Kombin.Api.Features.Profile;
using Kombin.Api.Features.Recommendation;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Catalog;

namespace Kombin.Api.Features.Social;

/// <summary>
/// The viewer's persona for match scores, by the catalog convention: an explicit <c>personaId</c> must be the signed-in caller's
/// (anonymous: <c>Profile.PersonaRequiresAuth</c>, someone else's or archived: <c>Profile.PersonaNotFound</c>); without one the active
/// persona, else the first, else none. <c>Learned</c> is false while the persona has no swipes: <c>MatchScorer</c> then caps every score at
/// 60, so the feed's "≥ 60" filter would empty the feed and is not applied.
/// </summary>
internal sealed record ViewerPersona(PersonaScoring? Scoring, bool Learned, Audience Audience)
{
    public static readonly ViewerPersona Anonymous = new(null, false, Audience.Unisex);

    public static async Task<Result<ViewerPersona>> LoadAsync(
        KombinDbContext db, Guid? viewerId, Guid? personaId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (viewerId is not { } userId)
        {
            return personaId is null ? Anonymous : ProfileErrors.PersonaRequiresAuth;
        }

        var profile = await db.UserProfiles.AsNoTracking()
            .Where(candidate => candidate.UserId == userId)
            .Select(candidate => new { candidate.ActivePersonaId, candidate.Audience })
            .SingleOrDefaultAsync(cancellationToken);
        var choice = await OutfitSources.ChoosePersonaAsync(db, userId, personaId, profile?.ActivePersonaId, now, cancellationToken);
        if (choice is null)
        {
            return ProfileErrors.PersonaNotFound;
        }

        return new ViewerPersona(choice.Scoring, choice.StyleProfile is { Confidence: > 0 }, profile?.Audience ?? Audience.Unisex);
    }
}
