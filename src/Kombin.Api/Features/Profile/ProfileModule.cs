using Kombin.Api.Features.Profile.ActivatePersona;
using Kombin.Api.Features.Profile.ArchivePersona;
using Kombin.Api.Features.Profile.ConfirmSize;
using Kombin.Api.Features.Profile.CreatePersona;
using Kombin.Api.Features.Profile.DeleteSize;
using Kombin.Api.Features.Profile.GetProfile;
using Kombin.Api.Features.Profile.GetSizeHint;
using Kombin.Api.Features.Profile.GetStyle;
using Kombin.Api.Features.Profile.ListPersonas;
using Kombin.Api.Features.Profile.ListSizes;
using Kombin.Api.Features.Profile.ListStyleCards;
using Kombin.Api.Features.Profile.SubmitSwipes;
using Kombin.Api.Features.Profile.UpdatePersona;
using Kombin.Api.Features.Profile.UpdateProfile;
using Kombin.Api.Features.Profile.UpsertSize;

namespace Kombin.Api.Features.Profile;

internal static class ProfileModule
{
    public static IEndpointRouteBuilder MapProfileEndpoints(this IEndpointRouteBuilder app)
    {
        var me = app.MapGroup("/api/v1/me").RequireAuthorization();

        var profile = me.MapGroup("/profile").WithTags("Profile");
        GetProfileEndpoint.Map(profile);
        UpdateProfileEndpoint.Map(profile);

        var personas = me.MapGroup("/personas").WithTags("Personas");
        ListPersonasEndpoint.Map(personas);
        CreatePersonaEndpoint.Map(personas);
        UpdatePersonaEndpoint.Map(personas);
        ArchivePersonaEndpoint.Map(personas);
        ActivatePersonaEndpoint.Map(personas);
        ListStyleCardsEndpoint.Map(personas);
        SubmitSwipesEndpoint.Map(personas);
        GetStyleEndpoint.Map(personas);

        var sizes = me.MapGroup("/sizes").WithTags("Sizes");
        ListSizesEndpoint.Map(sizes);
        UpsertSizeEndpoint.Map(sizes);
        DeleteSizeEndpoint.Map(sizes);
        GetSizeHintEndpoint.Map(sizes);
        ConfirmSizeEndpoint.Map(sizes);

        return app;
    }
}
