using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Results;
using Kombin.Api.Common.Storage;
using Kombin.Api.Common.Time;
using Kombin.Api.Features.League.GetMe;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.League;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Kombin.Api.Features.League.Join;

/// <summary>The consent version the player saw on the join screen; only the current one is accepted.</summary>
internal sealed record JoinLeagueRequest(string? ConsentVersion);

internal static class JoinLeagueEndpoint
{
    public static void Map(RouteGroupBuilder group) =>
        group.MapPost("/join", Handle)
            .WithName("JoinLeague")
            .Produces<LeagueMeResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

    /// <summary>
    /// "Lige katıl": records the consent (with the exact text shown, which is also the 18+ declaration) and makes the caller a player, in Bronze
    /// the first time, in their old tier after leaving. A known birth year that does not make the caller certainly <c>League:MinAge</c> or older is 403
    /// <c>League.AgeRestricted</c>. Idempotent: joining again under the current version changes nothing and writes no consent row.
    /// </summary>
    private static async Task<IResult> Handle(
        JoinLeagueRequest request,
        ICurrentUser currentUser,
        KombinDbContext db,
        IObjectStorage storage,
        IClock clock,
        IOptions<LeagueOptions> options,
        CancellationToken cancellationToken)
    {
        var current = LeagueConsent.Current;
        if (request.ConsentVersion != current.Version)
        {
            return LeagueErrors.ConsentVersionInvalid.ToHttpResult();
        }

        var userId = currentUser.UserId;
        var now = clock.UtcNow;
        var birthYear = await db.UserProfiles.AsNoTracking()
            .Where(profile => profile.UserId == userId)
            .Select(profile => profile.BirthYear)
            .FirstOrDefaultAsync(cancellationToken);

        if (LeagueRules.IsUnderAge(birthYear, Istanbul.Today(now), options.Value.MinAge))
        {
            return LeagueErrors.AgeRestricted.ToHttpResult();
        }

        var player = await db.LeaguePlayers.SingleOrDefaultAsync(row => row.UserId == userId, cancellationToken);
        if (player is { IsJoined: true } && player.ConsentVersion == current.Version)
        {
            return TypedResults.Ok(await GetLeagueMeEndpoint.BuildAsync(db, storage, options.Value, userId, now, cancellationToken));
        }

        if (player is null)
        {
            player = new LeaguePlayer { UserId = userId, Tier = LeagueTier.Bronze, JoinedAt = now };
            db.LeaguePlayers.Add(player);
        }
        else if (!player.IsJoined)
        {
            player.LeftAt = null;
            player.JoinedAt = now;
        }

        player.ConsentVersion = current.Version;
        db.LeagueConsents.Add(new LeagueConsentRecord { UserId = userId, Action = LeagueConsentAction.Join, Version = current.Version, Text = current.Text, At = now });

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // A double tap: the other request created the player (and wrote the consent row) first.
            db.ChangeTracker.Clear();
        }

        return TypedResults.Ok(await GetLeagueMeEndpoint.BuildAsync(db, storage, options.Value, userId, now, cancellationToken));
    }
}
