using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Http;
using Kombin.Api.Common.Results;
using Kombin.Api.Common.Time;
using Kombin.Api.Common.Validation;
using Kombin.Api.Features.Catalog;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Profile;
using Npgsql;

namespace Kombin.Api.Features.Profile.UpdatePersona;

/// <summary>
/// A partial update: a null field is left as it is, and a given list replaces the stored one (<c>[]</c> clears preferred/avoided
/// lists, but not seasons). <c>ClearBudget: true</c> removes both budget bounds.
/// </summary>
internal sealed record UpdatePersonaRequest(
    string? Name,
    string? Icon,
    int? SortOrder,
    decimal? BudgetMin,
    decimal? BudgetMax,
    bool? ClearBudget,
    int? FormalityMin,
    int? FormalityMax,
    IReadOnlyList<string>? PreferredColors,
    IReadOnlyList<string>? AvoidedColors,
    IReadOnlyList<string>? PreferredFits,
    IReadOnlyList<string>? Seasons);

internal static class UpdatePersonaEndpoint
{
    public static void Map(RouteGroupBuilder group) =>
        group.MapPatch("/{id:guid}", Handle)
            .WithName("UpdatePersona")
            .WithValidation<UpdatePersonaRequest>()
            .Produces<PersonaResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

    private static async Task<IResult> Handle(
        Guid id, UpdatePersonaRequest request, ICurrentUser currentUser, KombinDbContext db, IClock clock, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId;

        var persona = await db.Personas.OwnedBy(userId).SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (persona is null)
        {
            return ProfileErrors.PersonaNotFound.ToHttpResult();
        }

        if (request.Name is not null)
        {
            var name = request.Name.Trim();

            if (name != persona.Name
                && await db.Personas.OwnedBy(userId).AnyAsync(other => other.Name == name && other.Id != id, cancellationToken))
            {
                return ProfileErrors.PersonaNameTaken.ToHttpResult();
            }

            persona.Name = name;
        }

        persona.Icon = request.Icon?.Trim() ?? persona.Icon;
        persona.SortOrder = request.SortOrder ?? persona.SortOrder;

        if (request.ClearBudget == true)
        {
            persona.BudgetMin = null;
            persona.BudgetMax = null;
        }
        else
        {
            persona.BudgetMin = request.BudgetMin ?? persona.BudgetMin;
            persona.BudgetMax = request.BudgetMax ?? persona.BudgetMax;
        }

        persona.FormalityMin = (short)(request.FormalityMin ?? persona.FormalityMin);
        persona.FormalityMax = (short)(request.FormalityMax ?? persona.FormalityMax);

        if (request.PreferredColors is not null)
        {
            persona.PreferredColors = PersonaPreferences.Colors(request.PreferredColors);
        }

        if (request.AvoidedColors is not null)
        {
            persona.AvoidedColors = PersonaPreferences.Colors(request.AvoidedColors);
        }

        if (request.PreferredFits is not null)
        {
            persona.PreferredFits = PersonaPreferences.Fits(request.PreferredFits);
        }

        if (request.Seasons is not null)
        {
            persona.Seasons = EnumNames.ParseSeasons(request.Seasons);
        }

        // The stored values are only known to be consistent on their own; a partial update can break them in combination.
        var conflicts = new Dictionary<string, string[]>();

        if (persona.BudgetMin > persona.BudgetMax)
        {
            conflicts["budgetMax"] = ["'Budget Max' must not be below 'Budget Min'."];
        }

        if (persona.FormalityMin > persona.FormalityMax)
        {
            conflicts["formalityMax"] = ["'Formality Max' must not be below 'Formality Min'."];
        }

        if (persona.PreferredColors.Intersect(persona.AvoidedColors, StringComparer.Ordinal).Any())
        {
            conflicts["avoidedColors"] = ["A color cannot be both preferred and avoided."];
        }

        if (conflicts.Count > 0)
        {
            return Problems.Validation(httpContext, conflicts);
        }

        var preferencesChanged = request.PreferredColors is not null || request.AvoidedColors is not null || request.PreferredFits is not null;
        var styleProfile = await db.PersonaStyleProfiles.SingleOrDefaultAsync(profile => profile.PersonaId == id, cancellationToken);

        if (styleProfile is null || preferencesChanged)
        {
            var data = StyleProfileBuilder.Build(await StyleProfiles.LoadSamplesAsync(db, id, cancellationToken), persona);

            if (styleProfile is null)
            {
                styleProfile = new PersonaStyleProfile { PersonaId = id };
                db.PersonaStyleProfiles.Add(styleProfile);
            }

            StyleProfiles.Apply(styleProfile, data, clock.UtcNow);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Lost a race against a concurrent creation or rename to the same name.
            return ProfileErrors.PersonaNameTaken.ToHttpResult();
        }

        var activeId = await db.UserProfiles
            .AsNoTracking()
            .Where(profile => profile.UserId == userId)
            .Select(profile => profile.ActivePersonaId)
            .SingleOrDefaultAsync(cancellationToken);

        return Results.Ok(PersonaResponse.From(persona, styleProfile, persona.Id == activeId));
    }
}
