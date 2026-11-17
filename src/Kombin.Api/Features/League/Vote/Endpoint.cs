using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Results;
using Kombin.Api.Common.Time;
using Kombin.Api.Common.Validation;
using Kombin.Api.Features.League.Rules;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.League;
using Microsoft.Extensions.Options;

namespace Kombin.Api.Features.League.Vote;

/// <summary><c>Choice</c>: <c>left</c>, <c>right</c> or <c>skip</c> ("Karar veremedim"), any casing. <c>DecisionMs</c>: what the app measured from display to tap.</summary>
internal sealed record VoteRequest(string? Choice, int? DecisionMs);

/// <summary>
/// <c>Counted</c>: the vote counts (not a skip, not too fast). <c>ChosenWinRate</c> (0-100) and <c>ChosenGames</c>: the chosen outfit's real record
/// today, this vote included; the rate is null below <c>League:MinGamesForWinRate</c> games ("İlk oylardan biri seninki"), both are null for a
/// skip. The rest is the voter's progress: counted votes and served pairs today, the target for the voting points and the day's limit.
/// </summary>
internal sealed record VoteResponse(bool Counted, int? ChosenWinRate, int? ChosenGames, int CountedToday, int ServedToday, int Target, int Limit);

internal static class VoteEndpoint
{
    public static void Map(RouteGroupBuilder group) =>
        group.MapPost("/pairs/{pairId:guid}/vote", Handle)
            .WithName("VoteOnLeaguePair")
            .WithValidation<VoteRequest>()
            .Produces<VoteResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

    /// <summary>
    /// One vote on one served pair (design note section 7), in one transaction:
    /// <list type="bullet">
    /// <item>someone else's pair (or none): 404 <c>League.PairNotFound</c>;</item>
    /// <item>the day row is read <c>FOR SHARE</c> (the close will take it <c>FOR UPDATE</c>): from <c>closes_at</c> on, 409 <c>League.DayClosed</c>;</item>
    /// <item>answered already: the same choice returns the result again (200), another is 409 <c>League.AlreadyVoted</c>;</item>
    /// <item>counted only when not a skip and both the server's time since serving and the app's <c>decisionMs</c> reach
    /// <c>League:MinDecisionTime</c>; the voter's first <c>League:NewVoterVotes</c> counted votes weigh <c>League:NewVoterWeight</c>;</item>
    /// <item>a counted vote adds a comparison and the weight to both entries' games and to the chosen one's wins, in one statement that locks the
    /// two rows in ID order (as <c>pairs/next</c> does; <c>FOR NO KEY UPDATE</c>, compatible with the key-share locks of new pair rows), so two
    /// voters judging the same two outfits at once never deadlock.</item>
    /// </list>
    /// </summary>
    private static async Task<IResult> Handle(
        Guid pairId,
        VoteRequest request,
        ICurrentUser currentUser,
        KombinDbContext db,
        IClock clock,
        IOptions<LeagueOptions> options,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var voterId = currentUser.UserId;
        var choice = LeagueChoices.Parse(request.Choice!);
        var now = clock.UtcNow;

        var day = await db.LeaguePairs.AsNoTracking()
            .Where(pair => pair.Id == pairId && pair.VoterId == voterId)
            .Select(pair => (DateOnly?)pair.Day)
            .SingleOrDefaultAsync(cancellationToken);
        if (day is not { } pairDay)
        {
            return LeagueErrors.PairNotFound.ToHttpResult();
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var closesAt = LeagueDays.ClosesAt(pairDay, settings.DayClosesAt);
        await db.Database.ExecuteSqlAsync(
            $"INSERT INTO league.days (day, closes_at, ranked_count, engine_count, pair_count, bt_iterations, bt_converged, close_ms) VALUES ({pairDay}, {closesAt}, 0, 0, 0, 0, false, 0) ON CONFLICT (day) DO NOTHING",
            cancellationToken);
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM league.days WHERE day = {pairDay} FOR SHARE", cancellationToken);
        var dayRow = await db.LeagueDays.AsNoTracking().SingleAsync(row => row.Day == pairDay, cancellationToken);
        if (now >= dayRow.ClosesAt || dayRow.ClosedAt is not null)
        {
            return LeagueErrors.DayClosed.ToHttpResult();
        }

        // One vote at a time per pair: the row lock makes a double tap wait and then see the first answer.
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM league.pairs WHERE id = {pairId} FOR UPDATE", cancellationToken);
        var pair = await db.LeaguePairs.SingleAsync(row => row.Id == pairId, cancellationToken);

        if (pair.AnsweredAt is not null)
        {
            if (pair.Choice != choice)
            {
                return LeagueErrors.AlreadyVoted.ToHttpResult();
            }

            var again = await RespondAsync(pair);
            await transaction.CommitAsync(cancellationToken);

            return again;
        }

        var counted = choice != LeaguePairChoice.Skip
            && now - pair.ServedAt >= settings.MinDecisionTime
            && request.DecisionMs!.Value >= settings.MinDecisionTime.TotalMilliseconds;
        var weight = 0.0;

        if (counted)
        {
            var countedBefore = (await db.Database
                .SqlQuery<int>($"UPDATE league.players SET counted_votes = counted_votes + 1, updated_at = {now} WHERE user_id = {voterId} RETURNING counted_votes AS \"Value\"")
                .ToListAsync(cancellationToken)).Single() - 1;
            weight = countedBefore < settings.NewVoterVotes ? settings.NewVoterWeight : 1.0;
        }

        pair.AnsweredAt = now;
        pair.Choice = choice;
        pair.ClientDecisionMilliseconds = request.DecisionMs;
        pair.Counted = counted;
        pair.Weight = (float)weight;
        await db.SaveChangesAsync(cancellationToken);

        if (counted)
        {
            var chosen = choice == LeaguePairChoice.Left ? pair.LeftEntryId : pair.RightEntryId;
            await db.Database.ExecuteSqlAsync(
                $"""
                WITH ordered AS (
                    SELECT id FROM league.entries WHERE id IN ({pair.LeftEntryId}, {pair.RightEntryId}) ORDER BY id FOR NO KEY UPDATE)
                UPDATE league.entries AS entry
                SET comparisons = entry.comparisons + 1,
                    weighted_games = entry.weighted_games + {weight},
                    weighted_wins = entry.weighted_wins + CASE WHEN entry.id = {chosen} THEN {weight} ELSE 0 END
                FROM ordered WHERE entry.id = ordered.id
                """,
                cancellationToken);
        }

        var response = await RespondAsync(pair);
        await transaction.CommitAsync(cancellationToken);

        return response;

        async Task<IResult> RespondAsync(LeaguePair answered)
        {
            var skipped = answered.Choice == LeaguePairChoice.Skip;
            var chosenId = answered.Choice == LeaguePairChoice.Left ? answered.LeftEntryId : answered.RightEntryId;
            var record = await db.LeagueEntries.AsNoTracking()
                .Where(entry => entry.Id == chosenId)
                .Select(entry => new { entry.WeightedWins, entry.WeightedGames })
                .SingleAsync(cancellationToken);
            var chosen = WinRate.For(record.WeightedWins, record.WeightedGames, skipped, settings.MinGamesForWinRate);
            var stats = await LeagueVoterStats.ForDayAsync(db, voterId, answered.Day, cancellationToken);

            return TypedResults.Ok(new VoteResponse(answered.Counted, chosen.WinRate, chosen.Games, stats.Counted, stats.Served, settings.VotesForPoints, settings.MaxPairsPerDay));
        }
    }
}
