using Hangfire;
using Kombin.Api.Common.Auth;
using Kombin.Api.Features.Identity.CancelDeletion;
using Kombin.Api.Features.Identity.DeleteAccount;
using Kombin.Api.Features.Identity.DisableAccount;
using Kombin.Api.Features.Identity.EnableAccount;
using Kombin.Api.Features.Identity.ExternalSignIn;
using Kombin.Api.Features.Identity.ExternalTokens;
using Kombin.Api.Features.Identity.GetMe;
using Kombin.Api.Features.Identity.Jobs;
using Kombin.Api.Features.Identity.Login;
using Kombin.Api.Features.Identity.Logout;
using Kombin.Api.Features.Identity.RefreshToken;
using Kombin.Api.Features.Identity.Register;
using Kombin.Api.Features.Notification;
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
        services.AddScoped<AccountAccess>();

        // Account deletion: the request (DELETE /me, the admin panel), the purge that removes the data, and the hourly job that runs it.
        services.AddScoped<AccountDeletion>();
        services.AddScoped<AccountPurger>();
        services.AddScoped<AccountDeletionJob>();

        // Sign in with Google / Apple: the app sends the provider's ID token, we check it against the provider's published keys.
        // Singletons because each verifier caches its provider's JWKS for a day.
        services.AddOptions<ExternalAuthOptions>().BindConfiguration(ExternalAuthOptions.SectionName);
        services.AddHttpClient(JwksKeySource.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(5));
        services.AddSingleton<GoogleTokenVerifier>();
        services.AddSingleton<AppleTokenVerifier>();
        services.AddSingleton<IExternalTokenVerifier, ExternalTokenVerifier>();

        return services;
    }

    public static IEndpointRouteBuilder MapIdentityEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/v1/auth").WithTags("Auth");
        RegisterEndpoint.Map(auth);
        LoginEndpoint.Map(auth);
        ExternalSignInEndpoint.Map(auth);
        RefreshTokenEndpoint.Map(auth);
        LogoutEndpoint.Map(auth);
        CancelDeletionEndpoint.Map(auth);

        var me = app.MapGroup("/api/v1/me").WithTags("Me").RequireAuthorization();
        GetMeEndpoint.Map(me);
        DeleteAccountEndpoint.Map(me);

        // Internal: the founder's account switch (also used by the admin panel), with X-Internal-Key.
        var internalIdentity = app.MapGroup("/api/v1/internal/identity").WithTags("Identity (internal)").RequireInternalKey();
        DisableAccountEndpoint.Map(internalIdentity);
        EnableAccountEndpoint.Map(internalIdentity);

        return app;
    }

    /// <summary>
    /// Registers the hourly <c>account-deletion</c>. Call it after <c>UseSocialJobs</c>; like the other registrations it does nothing unless
    /// <c>Hangfire:Enabled</c> is true.
    /// </summary>
    public static void UseIdentityJobs(this WebApplication app)
    {
        if (!app.Configuration.GetValue<bool>(NotificationModule.HangfireEnabledKey))
        {
            return;
        }

        app.Services.GetRequiredService<IRecurringJobManager>().AddOrUpdate<AccountDeletionJob>(
            AccountDeletionJob.JobId,
            job => job.RunAsync(CancellationToken.None),
            AccountDeletionJob.Cron,
            new RecurringJobOptions { TimeZone = QuietHours.Zone });
    }
}
