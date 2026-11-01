using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Results;
using Kombin.Api.Common.Time;
using Kombin.Api.Persistence;

namespace Kombin.Api.Features.Profile.GetStyle;

/// <summary>The whole derived style profile, weights included. For debugging and the app's "tarzın" screen.</summary>
internal sealed record StyleResponse(
    Guid PersonaId,
    IReadOnlyDictionary<string, double> ColorWeights,
    IReadOnlyDictionary<string, double> FamilyWeights,
    IReadOnlyDictionary<string, double> FitWeights,
    decimal FormalityMean,
    IReadOnlyDictionary<string, double> SeasonWeights,
    int LikeCount,
    int DislikeCount,
    double Confidence,
    DateTimeOffset UpdatedAt);

internal static class GetStyleEndpoint
{
    public static void Map(RouteGroupBuilder group) =>
        group.MapGet("/{id:guid}/style", Handle)
            .WithName("GetStyle")
            .Produces<StyleResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

    private static async Task<IResult> Handle(Guid id, ICurrentUser currentUser, KombinDbContext db, IClock clock, CancellationToken cancellationToken)
    {
        var persona = await db.Personas.AsNoTracking().OwnedBy(currentUser.UserId).SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (persona is null)
        {
            return ProfileErrors.PersonaNotFound.ToHttpResult();
        }

        var profile = await StyleProfiles.LoadOrDeriveAsync(db, persona, clock.UtcNow, cancellationToken);

        return Results.Ok(new StyleResponse(
            profile.PersonaId,
            profile.ColorWeights,
            profile.FamilyWeights,
            profile.FitWeights,
            profile.FormalityMean,
            profile.SeasonWeights,
            profile.LikeCount,
            profile.DislikeCount,
            Math.Round(profile.Confidence, 4),
            profile.UpdatedAt));
    }
}
