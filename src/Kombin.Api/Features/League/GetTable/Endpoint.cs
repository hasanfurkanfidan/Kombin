using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Results;
using Kombin.Api.Common.Time;
using Kombin.Api.Features.League.Rules;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.League;
using Kombin.Api.Persistence.Entities.Recommendation;
using Kombin.Api.Persistence.Entities.Wardrobe;
using Microsoft.Extensions.Options;

namespace Kombin.Api.Features.League.GetTable;

/// <summary>
/// The caller's group table this week (screen 05, "Haftalık lig"). <c>Tier</c>: the caller's own league this week; <c>TierUp</c> /
/// <c>TierDown</c>: the leagues above and below it (null at Diamond / Bronze). <c>TodayPoints</c>: today's total once today is closed
/// (<c>TodayIsFinal</c>), else the usage points earned so far today. <c>PromoteCount</c> / <c>RelegateCount</c>: the bands as they stand
/// (<c>min(5, floor(competed / 3))</c>). <c>Rows</c>: <c>{ rank, points, isMe, alias }</c> in table order, nothing else; the app shows "Sen" for
/// <c>isMe</c>. Between Sunday's close and Monday 00:00 it is last week's final table.
/// </summary>
internal sealed record LeagueTableResponse(
    DateOnly WeekStart,
    DateTimeOffset EndsAt,
    LeagueTier Tier,
    int TodayPoints,
    bool TodayIsFinal,
    int PromoteCount,
    int RelegateCount,
    LeagueTier? TierUp,
    LeagueTier? TierDown,
    int Size,
    IReadOnlyList<LeagueTableRow> Rows);

internal static class GetTableEndpoint
{
    public static void Map(RouteGroupBuilder group) =>
        group.MapGet("/table", Handle)
            .WithName("GetLeagueTable")
            .Produces<LeagueTableResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

    /// <summary>
    /// The table of the week of today (Istanbul). A joined player who is in no group yet is placed first (the mid-week rule). Errors: 403
    /// <c>League.NotJoined</c>; 404 <c>League.NoGroup</c> (no group this week, e.g. joined after Sunday's close: their group starts Monday).
    /// </summary>
    private static async Task<IResult> Handle(
        ICurrentUser currentUser, KombinDbContext db, IClock clock, IOptions<LeagueOptions> options, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var userId = currentUser.UserId;
        if (!await db.LeaguePlayers.AnyAsync(player => player.UserId == userId && player.LeftAt == null, cancellationToken))
        {
            return LeagueErrors.NotJoined.ToHttpResult();
        }

        var now = clock.UtcNow;
        await LeagueGroups.PlaceOneAsync(db, settings, userId, now, cancellationToken);

        var today = Istanbul.Today(now);
        var view = await LeagueTables.ForMemberAsync(db, settings, userId, LeagueDays.WeekStart(today), cancellationToken);
        if (view is null)
        {
            return LeagueErrors.NoGroup.ToHttpResult();
        }

        var (todayPoints, final) = await TodayPointsAsync(db, settings, userId, today, now, cancellationToken);

        return TypedResults.Ok(new LeagueTableResponse(
            view.WeekStart,
            view.EndsAt,
            view.Tier,
            todayPoints,
            final,
            view.PromoteCount,
            view.RelegateCount,
            view.Tier == LeagueTier.Diamond ? null : WeeklyTable.Up(view.Tier),
            view.Tier == LeagueTier.Bronze ? null : WeeklyTable.Down(view.Tier),
            view.Rows.Count,
            view.Rows));
    }

    /// <summary>
    /// Today's total once today is closed (without vote points held back by a review), else what the caller has earned from using the app so
    /// far today: the voting, worn and piece points, by the same rules as the close (section 9).
    /// </summary>
    private static async Task<(int Points, bool Final)> TodayPointsAsync(
        KombinDbContext db, LeagueOptions settings, Guid userId, DateOnly today, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (await db.LeagueDays.AnyAsync(day => day.Day == today && day.ClosedAt != null, cancellationToken))
        {
            var score = await db.LeagueDailyScores.AsNoTracking().SingleOrDefaultAsync(row => row.UserId == userId && row.Day == today, cancellationToken);

            return (score is null ? 0 : score.UnderReview ? score.Total - score.VotePoints : score.Total, true);
        }

        var closesAt = LeagueDays.ClosesAt(today, settings.DayClosesAt);
        var until = now < closesAt ? now : closesAt;
        var start = LeagueDays.Start(today);
        var (pieceStart, _) = LeagueDays.PieceWindow(today, settings.DayClosesAt);
        var weekStart = LeagueDays.WeekStart(today);

        var counted = (await LeagueVoterStats.ForDayAsync(db, userId, today, cancellationToken)).Counted;
        var worn = await db.OutfitFeedback.AnyAsync(
            feedback => feedback.UserId == userId && feedback.Action == OutfitFeedbackAction.Worn && feedback.CreatedAt >= start && feedback.CreatedAt < until, cancellationToken);
        var piece = await db.WardrobeItems.AnyAsync(
            item => item.UserId == userId && item.CreatedAt >= pieceStart && item.CreatedAt < until && item.Status == WardrobeItemStatus.Ready && !item.IsArchived
                && !item.Warnings.Contains(WardrobeItem.NotGarmentWarning),
            cancellationToken);
        var pieceDays = await db.LeagueDailyScores.CountAsync(row => row.UserId == userId && row.Day >= weekStart && row.Day < today && row.PiecePoints > 0, cancellationToken);

        return (DailyPoints.For(new DailyActivity(null, counted, worn, piece, pieceDays), settings.Points, settings.VotesForPoints).Total, false);
    }
}
