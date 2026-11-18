using Hangfire;
using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Hosting;
using Kombin.Api.Common.RateLimiting;
using Kombin.Api.Common.Time;
using Kombin.Api.Features.League.GetMe;
using Kombin.Api.Features.League.GetResults;
using Kombin.Api.Features.League.GetTable;
using Kombin.Api.Features.League.Internal.CloseDay;
using Kombin.Api.Features.League.Internal.Review;
using Kombin.Api.Features.League.Internal.ReviewQueue;
using Kombin.Api.Features.League.Jobs;
using Kombin.Api.Features.League.Join;
using Kombin.Api.Features.League.Leave;
using Kombin.Api.Features.League.NextPair;
using Kombin.Api.Features.League.Report;
using Kombin.Api.Features.League.Vote;
using Kombin.Api.Features.League.WithdrawEntry;
using Microsoft.Extensions.Options;

namespace Kombin.Api.Features.League;

/// <summary>
/// Kombin Ligi (design: <c>docs/design/league.md</c>). PR 1 (task L2) brought the schema, the settings, the consent and joining; PR 2 (task L3)
/// entering (the worn outfit, the safety check, the image copies, <c>league-entries</c>), the engine fill (<c>league-fill</c>) and voting; PR 3
/// (task L4) the day close (<c>league-close</c>, <see cref="DayCloser"/>), the results and the purge (<c>league-purge</c>). The weekly leagues
/// come next.
/// </summary>
internal static class LeagueModule
{
    public static IServiceCollection AddLeagueFeature(this IServiceCollection services)
    {
        services.AddOptions<LeagueOptions>().BindConfiguration(LeagueOptions.SectionName).ValidateOnStart();
        services.AddSingleton<IValidateOptions<LeagueOptions>, LeagueOptionsValidator>();

        services.AddScoped<EntryMaterializer>();
        services.AddScoped<LeagueEntriesJob>();
        services.AddScoped<LeagueFillJob>();
        services.AddScoped<DayCloser>();
        services.AddScoped<LeagueEntryReviewer>();
        services.AddScoped<WeekCloser>();
        services.AddScoped<LeagueCloseJob>();
        services.AddScoped<LeaguePurgeJob>();

        return services;
    }

    /// <summary>
    /// The routes are always mapped, so the OpenAPI document and the authorization matrix see them; with <c>League:Enabled</c> false they answer
    /// 404 <c>League.Disabled</c> before anything else runs. Serving, voting and reporting share the <c>league</c> rate-limit policy (a client in a
    /// loop; 30 pairs a day is the real cap).
    /// </summary>
    public static IEndpointRouteBuilder MapLeagueEndpoints(this IEndpointRouteBuilder app)
    {
        var league = app.MapGroup("/api/v1/league")
            .RequireAuthorization()
            .WithTags("League")
            .AddEndpointFilter<RouteGroupBuilder, FeatureEnabledFilter<LeagueOptions>>();

        GetLeagueMeEndpoint.Map(league);
        JoinLeagueEndpoint.Map(league);
        LeaveLeagueEndpoint.Map(league);
        WithdrawEntryEndpoint.Map(league);

        GetResultsEndpoint.Map(league);
        GetTableEndpoint.Map(league);

        var voting = league.MapGroup(string.Empty).RequireRateLimiting(RateLimitPolicies.League);
        NextPairEndpoint.Map(voting);
        VoteEndpoint.Map(voting);
        ReportEntryEndpoint.Map(voting);

        // Internal (operators and workers): the internal key first, then the league switch.
        var internalLeague = app.MapGroup("/api/v1/internal/league")
            .WithTags("League (internal)")
            .RequireInternalKey()
            .AddEndpointFilter<RouteGroupBuilder, FeatureEnabledFilter<LeagueOptions>>();
        CloseDayEndpoint.Map(internalLeague);
        ReviewQueueEndpoint.Map(internalLeague);
        ReviewEntryEndpoint.Map(internalLeague);

        return app;
    }

    /// <summary>
    /// <c>league-entries</c> (every minute), <c>league-fill</c> (every 15 minutes, 00:00-20:45 Istanbul), <c>league-close</c> (hourly at :00) and
    /// <c>league-purge</c> (daily 04:15), behind <c>Hangfire:Enabled</c> and
    /// <c>League:Enabled</c>: with the league off they are not registered (and removed if an earlier start registered them). Called in
    /// <c>Program.cs</c> after <c>UseIdentityJobs</c>.
    /// </summary>
    public static void UseLeagueJobs(this WebApplication app)
    {
        if (!app.Configuration.GetValue<bool>(HangfireSettings.EnabledKey))
        {
            return;
        }

        var jobs = app.Services.GetRequiredService<IRecurringJobManager>();
        if (!app.Services.GetRequiredService<IOptions<LeagueOptions>>().Value.Enabled)
        {
            jobs.RemoveIfExists(LeagueEntriesJob.JobId);
            jobs.RemoveIfExists(LeagueFillJob.JobId);
            jobs.RemoveIfExists(LeagueCloseJob.JobId);
            jobs.RemoveIfExists(LeaguePurgeJob.JobId);
            return;
        }

        var schedule = new RecurringJobOptions { TimeZone = Istanbul.Zone };
        jobs.AddOrUpdate<LeagueEntriesJob>(LeagueEntriesJob.JobId, job => job.RunAsync(CancellationToken.None), LeagueEntriesJob.Cron, schedule);
        jobs.AddOrUpdate<LeagueFillJob>(LeagueFillJob.JobId, job => job.RunAsync(CancellationToken.None), LeagueFillJob.Cron, schedule);
        jobs.AddOrUpdate<LeagueCloseJob>(LeagueCloseJob.JobId, job => job.RunAsync(CancellationToken.None), LeagueCloseJob.Cron, schedule);
        jobs.AddOrUpdate<LeaguePurgeJob>(LeaguePurgeJob.JobId, job => job.RunAsync(CancellationToken.None), LeaguePurgeJob.Cron, schedule);
    }
}
