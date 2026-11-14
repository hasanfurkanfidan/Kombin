using Kombin.Api.Common.Results;

namespace Kombin.Api.Features.Profile;

internal static class ProfileErrors
{
    // Also the answer for a persona that belongs to someone else or is archived: whether it exists is none of the caller's business.
    public static readonly Error PersonaNotFound =
        Error.NotFound("Profile.PersonaNotFound", "The style does not exist.");

    public static readonly Error PersonaLimit =
        Error.Validation("Profile.PersonaLimit", $"A user may have at most {Persistence.Entities.Profile.Persona.MaxActivePerUser} styles.");

    public static readonly Error PersonaNameTaken =
        Error.Conflict("Profile.PersonaNameTaken", "You already have a style with this name.");

    public static readonly Error StyleCardNotFound =
        Error.NotFound("Profile.StyleCardNotFound", "At least one of the style cards does not exist.");

    public static readonly Error SizeEntryNotFound =
        Error.NotFound("Profile.SizeEntryNotFound", "The size entry does not exist.");

    public static readonly Error PersonaRequiresAuth =
        Error.Validation("Profile.PersonaRequiresAuth", "A style can only be chosen by a signed-in user.");
}
