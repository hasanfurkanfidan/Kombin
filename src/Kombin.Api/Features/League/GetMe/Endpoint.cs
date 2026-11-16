using Kombin.Api.Common.Auth;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.League;

namespace Kombin.Api.Features.League.GetMe;

/// <summary>The consent text the join screen shows, and its version (sent back by <c>POST /league/join</c>).</summary>
internal sealed record LeagueConsentResponse(string Version, string Text);

/// <summary>
/// The caller's league state. <c>Joined</c>: in the league now. <c>ConsentRequired</c>: the join screen must be shown (never joined, left, or
/// joined under an older consent version). <c>Tier</c> is null for someone who never joined; a player who left keeps theirs. Later tasks add the
/// day (entry, votes) and the week (group rank) to this response.
/// </summary>
internal sealed record LeagueMeResponse(bool Joined, bool ConsentRequired, LeagueConsentResponse Consent, LeagueTier? Tier, DateTimeOffset? JoinedAt);

internal static class GetLeagueMeEndpoint
{
    public static void Map(RouteGroupBuilder group) =>
        group.MapGet("/me", Handle)
            .WithName("GetLeagueMe")
            .Produces<LeagueMeResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

    /// <summary>The state of <paramref name="userId"/>; <c>POST /league/join</c> answers with it too.</summary>
    public static async Task<LeagueMeResponse> BuildAsync(KombinDbContext db, Guid userId, CancellationToken cancellationToken)
    {
        var player = await db.LeaguePlayers.AsNoTracking().SingleOrDefaultAsync(row => row.UserId == userId, cancellationToken);

        return Build(player);
    }

    public static LeagueMeResponse Build(LeaguePlayer? player)
    {
        var current = LeagueConsent.Current;
        var joined = player is { IsJoined: true };

        return new LeagueMeResponse(
            joined,
            !joined || player!.ConsentVersion != current.Version,
            new LeagueConsentResponse(current.Version, current.Text),
            player?.Tier,
            joined ? player!.JoinedAt : null);
    }

    private static async Task<IResult> Handle(ICurrentUser currentUser, KombinDbContext db, CancellationToken cancellationToken) =>
        TypedResults.Ok(await BuildAsync(db, currentUser.UserId, cancellationToken));
}
