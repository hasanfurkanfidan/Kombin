using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Results;
using Kombin.Api.Common.Time;
using Kombin.Api.Common.Validation;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.League;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Kombin.Api.Features.League.Report;

/// <summary><c>Side</c>: <c>left</c> or <c>right</c>, the outfit of the pair being reported. <c>Reason</c>: Inappropriate, NotClothing or Other (optional).</summary>
internal sealed record ReportEntryRequest(string? Side, string? Reason);

internal sealed record ReportEntryResponse(bool Reported);

internal static class ReportEntryEndpoint
{
    public static void Map(RouteGroupBuilder group) =>
        group.MapPost("/pairs/{pairId:guid}/report", Handle)
            .WithName("ReportLeagueOutfit")
            .WithValidation<ReportEntryRequest>()
            .Produces<ReportEntryResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

    /// <summary>
    /// Reports one outfit of a pair served to the caller (design note section 13): the pair is the only handle a voter has, so someone else's
    /// pair is 404 <c>League.PairNotFound</c>. One report per player and outfit (409 <c>League.AlreadyReported</c>); the reporter never sees that
    /// outfit again, and a pair still open is used up as a skip (never counted), so asking for the next pair does not show it again. The report
    /// counts toward the hide only from a trusted reporter (account at least <c>League:Reports:MinAccountAge</c> old and at least
    /// <c>League:Reports:MinCountedVotes</c> counted votes); <c>League:Reports:HideAt</c> counting reports take the outfit out of every pool until
    /// a review. The reporter learns nothing about either.
    /// </summary>
    private static async Task<IResult> Handle(
        Guid pairId,
        ReportEntryRequest request,
        ICurrentUser currentUser,
        KombinDbContext db,
        IClock clock,
        IOptions<LeagueOptions> options,
        CancellationToken cancellationToken)
    {
        var rules = options.Value.Reports;
        var reporterId = currentUser.UserId;
        var pair = await db.LeaguePairs.SingleOrDefaultAsync(row => row.Id == pairId && row.VoterId == reporterId, cancellationToken);
        if (pair is null)
        {
            return LeagueErrors.PairNotFound.ToHttpResult();
        }

        var now = clock.UtcNow;
        var entryId = LeagueChoices.Parse(request.Side!) == LeaguePairChoice.Left ? pair.LeftEntryId : pair.RightEntryId;
        var reporter = await db.Users.AsNoTracking()
            .Where(user => user.Id == reporterId)
            .Select(user => new
            {
                user.CreatedAt,
                CountedVotes = db.LeaguePlayers.Where(player => player.UserId == user.Id).Select(player => (int?)player.CountedVotes).FirstOrDefault() ?? 0,
            })
            .SingleAsync(cancellationToken);
        var counts = now - reporter.CreatedAt >= rules.MinAccountAge && reporter.CountedVotes >= rules.MinCountedVotes;
        LeagueReportReason? reason = LeagueChoices.TryParse<LeagueReportReason>(request.Reason, out var parsed) ? parsed : null;

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        db.LeagueReports.Add(new LeagueReport { EntryId = entryId, ReporterId = reporterId, Reason = reason, Counts = counts, CreatedAt = now });

        if (pair.AnsweredAt is null)
        {
            pair.AnsweredAt = now;
            pair.Choice = LeaguePairChoice.Skip;
            pair.Counted = false;
            pair.Weight = 0;
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return LeagueErrors.AlreadyReported.ToHttpResult();
        }

        if (counts)
        {
            await db.LeagueEntries.Where(entry => entry.Id == entryId).ExecuteUpdateAsync(setters => setters
                .SetProperty(entry => entry.CountingReports, entry => entry.CountingReports + 1)
                .SetProperty(entry => entry.UpdatedAt, now), cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        return TypedResults.Accepted((string?)null, new ReportEntryResponse(true));
    }
}
