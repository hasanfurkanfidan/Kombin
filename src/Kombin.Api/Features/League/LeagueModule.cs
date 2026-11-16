using Kombin.Api.Common.Results;
using Kombin.Api.Features.League.GetMe;
using Kombin.Api.Features.League.Join;
using Kombin.Api.Features.League.Leave;
using Microsoft.Extensions.Options;

namespace Kombin.Api.Features.League;

/// <summary>
/// Kombin Ligi (design: <c>docs/design/league.md</c>). Task L2 (PR 1) brings the schema, the settings, the consent and joining; entries, voting,
/// the closes and their jobs come in the next PRs.
/// </summary>
internal static class LeagueModule
{
    public static IServiceCollection AddLeagueFeature(this IServiceCollection services)
    {
        services.AddOptions<LeagueOptions>().BindConfiguration(LeagueOptions.SectionName).ValidateOnStart();
        services.AddSingleton<IValidateOptions<LeagueOptions>, LeagueOptionsValidator>();

        return services;
    }

    /// <summary>
    /// The routes are always mapped, so the OpenAPI document and the authorization matrix see them; with <c>League:Enabled</c> false they answer
    /// 404 <c>League.Disabled</c> before anything else runs.
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

        return app;
    }
}

/// <summary>Answers 404 <c>League.Disabled</c> while <c>League:Enabled</c> is false (the default until the app screens ship).</summary>
internal sealed class LeagueEnabledFilter(IOptionsMonitor<LeagueOptions> options) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next) =>
        options.CurrentValue.Enabled ? await next(context) : LeagueErrors.Disabled.ToHttpResult();
}
