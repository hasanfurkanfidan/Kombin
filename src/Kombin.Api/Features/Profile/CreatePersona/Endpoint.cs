using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Results;
using Kombin.Api.Common.Time;
using Kombin.Api.Common.Validation;
using Kombin.Api.Features.Catalog;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Profile;
using Npgsql;

namespace Kombin.Api.Features.Profile.CreatePersona;

/// <summary>Every field but the name and icon is optional. Seasons default to all four, formality to 1-5.</summary>
internal sealed record CreatePersonaRequest(
    string Name,
    string Icon,
    decimal? BudgetMin,
    decimal? BudgetMax,
    int? FormalityMin,
    int? FormalityMax,
    IReadOnlyList<string>? PreferredColors,
    IReadOnlyList<string>? AvoidedColors,
    IReadOnlyList<string>? PreferredFits,
    IReadOnlyList<string>? Seasons);

internal static class CreatePersonaEndpoint
{
    private const int SortOrderStep = 10;

    public static void Map(RouteGroupBuilder group) =>
        group.MapPost("/", Handle)
            .WithName("CreatePersona")
            .WithValidation<CreatePersonaRequest>()
            .Produces<PersonaResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status409Conflict);

    private static async Task<IResult> Handle(
        CreatePersonaRequest request, ICurrentUser currentUser, KombinDbContext db, IClock clock, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId;
        var profile = await UserProfiles.GetOrCreateAsync(db, userId, cancellationToken);

        var existing = db.Personas.OwnedBy(userId);

        if (await existing.CountAsync(cancellationToken) >= Persona.MaxActivePerUser)
        {
            return ProfileErrors.PersonaLimit.ToHttpResult();
        }

        var name = request.Name.Trim();

        if (await existing.AnyAsync(persona => persona.Name == name, cancellationToken))
        {
            return ProfileErrors.PersonaNameTaken.ToHttpResult();
        }

        var persona = new Persona
        {
            UserId = userId,
            Name = name,
            Icon = request.Icon.Trim(),
            SortOrder = (await existing.MaxAsync(other => (int?)other.SortOrder, cancellationToken) ?? 0) + SortOrderStep,
            BudgetMin = request.BudgetMin,
            BudgetMax = request.BudgetMax,
            FormalityMin = (short)(request.FormalityMin ?? 1),
            FormalityMax = (short)(request.FormalityMax ?? 5),
            PreferredColors = PersonaPreferences.Colors(request.PreferredColors ?? []),
            AvoidedColors = PersonaPreferences.Colors(request.AvoidedColors ?? []),
            PreferredFits = PersonaPreferences.Fits(request.PreferredFits ?? []),
            Seasons = request.Seasons is { Count: > 0 } seasons ? EnumNames.ParseSeasons(seasons) : Season.AllYear,
        };

        var styleProfile = new PersonaStyleProfile { PersonaId = persona.Id };
        StyleProfiles.Apply(styleProfile, StyleProfileBuilder.Build([], persona), clock.UtcNow);

        db.Personas.Add(persona);
        db.PersonaStyleProfiles.Add(styleProfile);

        // The first persona is the one the app opens with.
        var becomesActive = profile.ActivePersonaId is null;
        if (becomesActive)
        {
            profile.ActivePersonaId = persona.Id;
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Lost a race against a concurrent creation of a persona with the same name.
            return ProfileErrors.PersonaNameTaken.ToHttpResult();
        }

        return Results.Json(PersonaResponse.From(persona, styleProfile, becomesActive), statusCode: StatusCodes.Status201Created);
    }
}
