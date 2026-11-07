using Hangfire;
using Kombin.Api.Common.Auth;
using Kombin.Api.Features.Notification;
using Kombin.Api.Features.Social.Consent;
using Kombin.Api.Features.Social.CreateOutfitPost;
using Kombin.Api.Features.Social.CreateUploadUrl;
using Kombin.Api.Features.Social.Creators;
using Kombin.Api.Features.Social.GetFeed;
using Kombin.Api.Features.Social.Jobs;
using Kombin.Api.Features.Social.Posts;
using Kombin.Api.Features.Social.ProcessPost;
using Kombin.Api.Features.Social.PublishPost;
using Kombin.Api.Features.Social.ReportPost;
using Kombin.Api.Features.Social.Review;
using Kombin.Api.Features.Social.TryWithWardrobe;
using Kombin.Api.Features.Social.Users;
using Microsoft.Extensions.Options;

namespace Kombin.Api.Features.Social;

internal static class SocialModule
{
    public static IServiceCollection AddSocialFeature(this IServiceCollection services)
    {
        services.AddOptions<CreatorOptions>().BindConfiguration(CreatorOptions.SectionName).ValidateOnStart();
        services.AddSingleton<IValidateOptions<CreatorOptions>, CreatorOptionsValidator>();

        // Plain classes with constructor injection, like the other jobs: Hangfire activates them from a scope, the tests resolve them directly.
        services.AddScoped<PostProcessJob>();
        services.AddScoped<SocialPurgeJob>();

        return services;
    }

    public static IEndpointRouteBuilder MapSocialEndpoints(this IEndpointRouteBuilder app)
    {
        ConsentEndpoint.Map(app.MapGroup("/api/v1/social").RequireAuthorization().WithTags("Social"));

        // Two groups on one prefix: the post flow needs a user; reading a post works signed out (public posts only).
        var authoring = app.MapGroup("/api/v1/posts").RequireAuthorization().WithTags("Social");
        CreatePostUploadUrlEndpoint.Map(authoring);
        ProcessPostEndpoint.Map(authoring);
        CreateOutfitPostEndpoint.Map(authoring);
        PublishPostEndpoint.Map(authoring);
        ReportPostEndpoint.Map(authoring);
        TryWithWardrobeEndpoint.Map(authoring);

        var posts = app.MapGroup("/api/v1/posts").WithTags("Social");
        PostsEndpoint.MapPosts(posts);

        var me = app.MapGroup("/api/v1/me").RequireAuthorization().WithTags("Social");
        PostsEndpoint.MapMine(me);

        GetFeedEndpoint.Map(app.MapGroup("/api/v1").WithTags("Social"));
        UsersEndpoint.Map(app.MapGroup("/api/v1/users").WithTags("Social"), me);

        var internalSocial = app.MapGroup("/api/v1/internal/social").WithTags("Social (internal)").RequireInternalKey();
        CreatorsEndpoint.Map(app.MapGroup("/api/v1/creators").WithTags("Social"), me, internalSocial);
        ReviewEndpoint.Map(internalSocial);

        return app;
    }

    /// <summary>
    /// Registers the hourly <c>social-purge</c>. Call it after <c>UseWardrobeJobs</c>; like the other registrations it does nothing unless
    /// <c>Hangfire:Enabled</c> is true. <c>post-process</c> is not recurring: <c>process</c> and <c>from-outfit</c> enqueue it per post.
    /// </summary>
    public static void UseSocialJobs(this WebApplication app)
    {
        if (!app.Configuration.GetValue<bool>(NotificationModule.HangfireEnabledKey))
        {
            return;
        }

        app.Services.GetRequiredService<IRecurringJobManager>().AddOrUpdate<SocialPurgeJob>(
            SocialPurgeJob.JobId,
            job => job.RunAsync(CancellationToken.None),
            SocialPurgeJob.Cron,
            new RecurringJobOptions { TimeZone = QuietHours.Zone });
    }
}
