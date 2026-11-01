using Kombin.Api.Common.Auth;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Profile;

namespace Kombin.Api.Features.Profile.GetProfile;

internal sealed record ProfileResponse(
    Audience Audience,
    int? BirthYear,
    int? HeightCm,
    decimal? WeightKg,
    int? ChestCm,
    int? WaistCm,
    int? HipCm,
    decimal? ShoeSizeEu,
    PersonaRef? ActivePersona,
    int PersonaCount,
    bool OnboardingCompleted,
    DateTimeOffset? OnboardingCompletedAt)
{
    public static async Task<ProfileResponse> BuildAsync(KombinDbContext db, UserProfile profile, CancellationToken cancellationToken)
    {
        var active = profile.ActivePersonaId is { } activeId
            ? await db.Personas
                .AsNoTracking()
                .OwnedBy(profile.UserId)
                .Where(persona => persona.Id == activeId)
                .Select(persona => new PersonaRef(persona.Id, persona.Name, persona.Icon))
                .SingleOrDefaultAsync(cancellationToken)
            : null;

        var personaCount = await db.Personas.AsNoTracking().OwnedBy(profile.UserId).CountAsync(cancellationToken);

        return new ProfileResponse(
            profile.Audience,
            profile.BirthYear,
            profile.HeightCm,
            profile.WeightKg,
            profile.ChestCm,
            profile.WaistCm,
            profile.HipCm,
            profile.ShoeSizeEu,
            active,
            personaCount,
            profile.OnboardingCompletedAt is not null,
            profile.OnboardingCompletedAt);
    }
}

internal static class GetProfileEndpoint
{
    public static void Map(RouteGroupBuilder group) =>
        group.MapGet("/", Handle)
            .WithName("GetProfile")
            .Produces<ProfileResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

    private static async Task<IResult> Handle(ICurrentUser currentUser, KombinDbContext db, CancellationToken cancellationToken)
    {
        var profile = await UserProfiles.GetOrCreateAsync(db, currentUser.UserId, cancellationToken);

        return Results.Ok(await ProfileResponse.BuildAsync(db, profile, cancellationToken));
    }
}
