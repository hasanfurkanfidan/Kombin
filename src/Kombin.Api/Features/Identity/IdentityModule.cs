using Kombin.Api.Features.Identity.GetMe;
using Kombin.Api.Features.Identity.Login;
using Kombin.Api.Features.Identity.Logout;
using Kombin.Api.Features.Identity.RefreshToken;
using Kombin.Api.Features.Identity.Register;
using Kombin.Api.Persistence.Entities.Identity;
using Microsoft.AspNetCore.Identity;

namespace Kombin.Api.Features.Identity;

internal static class IdentityModule
{
    public static IServiceCollection AddIdentityFeature(this IServiceCollection services)
    {
        // PBKDF2 hashing from the framework; no ASP.NET Identity tables or UserManager involved.
        services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();
        services.AddScoped<AuthTokenIssuer>();

        return services;
    }

    public static IEndpointRouteBuilder MapIdentityEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/v1/auth").WithTags("Auth");
        RegisterEndpoint.Map(auth);
        LoginEndpoint.Map(auth);
        RefreshTokenEndpoint.Map(auth);
        LogoutEndpoint.Map(auth);

        var me = app.MapGroup("/api/v1/me").WithTags("Me").RequireAuthorization();
        GetMeEndpoint.Map(me);

        return app;
    }
}
