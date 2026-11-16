using Hangfire;
using Kombin.Api.Common.Hosting;
using Kombin.Api.Common.RateLimiting;
using Kombin.Api.Common.Results;
using Kombin.Api.Common.Time;
using Kombin.Api.Features.League.GetMe;
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
/// entering (the worn outfit, the safety check, the image copies, <c>league-entries</c>), the engine fill (<c>league-fill</c>) and voting. The
/// closes, the results and the weekly leagues come next.
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
            .AddEndpointFilter<RouteGroupBuilder, LeagueEnabledFilter>();

        GetLeagueMeEndpoint.Map(league);
        JoinLeagueEndpoint.Map(league);
        LeaveLeagueEndpoint.Map(league);
        WithdrawEntryEndpoint.Map(league);

        var voting = league.MapGroup(string.Empty).RequireRateLimiting(RateLimitPolicies.League);
        NextPairEndpoint.Map(voting);
        VoteEndpoint.Map(voting);
        ReportEntryEndpoint.Map(voting);

        return app;
    }

    /// <summary>
    /// <c>league-entries</c> (every minute) and <c>league-fill</c> (every 15 minutes, 00:00-20:45 Istanbul), behind <c>Hangfire:Enabled</c> and
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
            return;
        }

        var schedule = new RecurringJobOptions { TimeZone = Istanbul.Zone };
        jobs.AddOrUpdate<LeagueEntriesJob>(LeagueEntriesJob.JobId, job => job.RunAsync(CancellationToken.None), LeagueEntriesJob.Cron, schedule);
        jobs.AddOrUpdate<LeagueFillJob>(LeagueFillJob.JobId, job => job.RunAsync(CancellationToken.None), LeagueFillJob.Cron, schedule);
    }
}

/// <summary>Answers 404 <c>League.Disabled</c> while <c>League:Enabled</c> is false (the default until the app screens ship).</summary>
internal sealed class LeagueEnabledFilter(IOptionsMonitor<LeagueOptions> options) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next) =>
        options.CurrentValue.Enabled ? await next(context) : LeagueErrors.Disabled.ToHttpResult();
}
