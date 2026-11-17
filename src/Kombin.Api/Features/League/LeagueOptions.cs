using Microsoft.Extensions.Options;

namespace Kombin.Api.Features.League;

/// <summary>
/// <c>League:*</c>: every number of Kombin Ligi (design: <c>docs/design/league.md</c>, section 14). The rules file asks for settings, not
/// constants: the numbers come from small simulations and are revisited after the first month. <c>LeagueOptionsTests</c> pins the defaults
/// to the design note.
/// </summary>
internal sealed class LeagueOptions
{
    public const string SectionName = "League";

    /// <summary>Off until the app screens ship: every league route answers 404 <c>League.Disabled</c> and no league job runs.</summary>
    public bool Enabled { get; set; }

    /// <summary>The day closes (votes stop, results are computed) at this Istanbul time.</summary>
    public TimeOnly DayClosesAt { get; set; } = new(21, 0);

    /// <summary>
    /// How many past days the hourly <c>league-close</c> looks at for one that has not been closed (a deploy, a crash); an older day is closed
    /// by hand (<c>POST /internal/league/days/{date}/close</c>).
    /// </summary>
    public int CloseCatchUpDays { get; set; } = 7;

    /// <summary>A known birth year keeps the player out unless they are certainly older than this (<c>LeagueRules.IsUnderAge</c>).</summary>
    public int MinAge { get; set; } = 18;

    public int MaxPairsPerDay { get; set; } = 30;

    /// <summary>Counted votes in a day that earn <see cref="LeaguePoints.Voting"/>.</summary>
    public int VotesForPoints { get; set; } = 10;

    /// <summary>A vote given sooner after the pair appeared is not counted.</summary>
    public TimeSpan MinDecisionTime { get; set; } = TimeSpan.FromMilliseconds(600);

    /// <summary>An account's first counted votes weigh <see cref="NewVoterWeight"/>.</summary>
    public int NewVoterVotes { get; set; } = 20;

    public double NewVoterWeight { get; set; } = 0.5;

    /// <summary>Below this many (weighted) games the vote response shows no win rate ("İlk oylardan biri seninki").</summary>
    public int MinGamesForWinRate { get; set; } = 5;

    public int PairCandidates { get; set; } = 200;

    /// <summary>Counted comparisons an outfit needs to be ranked.</summary>
    public int MinComparisons { get; set; } = 8;

    /// <summary>The virtual start: half a win and half a loss against an average opponent.</summary>
    public double PriorWins { get; set; } = 0.5;

    public double PriorLosses { get; set; } = 0.5;

    public double BtTolerance { get; set; } = 1e-6;

    public int BtMaxIterations { get; set; } = 500;

    public LeaguePoints Points { get; set; } = new();

    /// <summary>The week's points are the best this many daily totals.</summary>
    public int BestDays { get; set; } = 5;

    public int GroupSize { get; set; } = 30;

    public int GroupMaxSize { get; set; } = 35;

    public int PromoteCount { get; set; } = 5;

    public int RelegateCount { get; set; } = 5;

    /// <summary>A tier with fewer active players is merged with its neighbour into one group.</summary>
    public int MinTierPlayers { get; set; } = 15;

    public int IdleWeeksToRelegate { get; set; } = 2;

    /// <summary>The same piece set competes once in this many days.</summary>
    public int RepeatExactDays { get; set; } = 30;

    /// <summary>An outfit at most <see cref="RepeatNearMaxDifference"/> pieces apart counts as the same within this many days.</summary>
    public int RepeatNearDays { get; set; } = 7;

    public int RepeatNearMaxDifference { get; set; } = 1;

    /// <summary>Non-accessory wardrobe pieces an outfit needs to compete.</summary>
    public int MinPieces { get; set; } = 2;

    public int ModerationRetriesPerRun { get; set; } = 50;

    public LeagueEngineFill EngineFill { get; set; } = new();

    public LeagueReports Reports { get; set; } = new();

    public LeagueStyleScoreOptions StyleScore { get; set; } = new();

    public LeagueRetention Retention { get; set; } = new();
}

/// <summary>The daily points: up to 70 from votes, up to 30 from using the app.</summary>
internal sealed class LeaguePoints
{
    public int VotesMax { get; set; } = 70;

    public int Voting { get; set; } = 15;

    public int Worn { get; set; } = 10;

    public int Piece { get; set; } = 5;

    /// <summary>Days of a week whose piece additions count.</summary>
    public int PieceDaysPerWeek { get; set; } = 3;
}

internal sealed class LeagueEngineFill
{
    /// <summary>Engine outfits top a bucket up to this many competing entries.</summary>
    public int MinPool { get; set; } = 30;

    public int MaxPerRun { get; set; } = 20;
}

internal sealed class LeagueReports
{
    /// <summary>Counting reports that take an entry out of the pool until it is reviewed.</summary>
    public int HideAt { get; set; } = 3;

    public TimeSpan MinAccountAge { get; set; } = TimeSpan.FromHours(72);

    public int MinCountedVotes { get; set; } = 20;
}

internal sealed class LeagueStyleScoreOptions
{
    public int WindowDays { get; set; } = 90;

    /// <summary>Only days with at least this many ranked human outfits count.</summary>
    public int MinRankedPerDay { get; set; } = 30;

    public int MinDays { get; set; } = 30;

    public double Threshold { get; set; } = 75;

    public int ConsecutiveWeeks { get; set; } = 4;
}

internal sealed class LeagueRetention
{
    public int ImageDays { get; set; } = 7;

    public int PairDays { get; set; } = 30;

    public int EntryDays { get; set; } = 90;

    /// <summary>A stored copy under <c>league/</c> that no entry piece names is deleted by <c>league-purge</c> once it is this old (a younger one may
    /// belong to an entry whose transaction has not committed yet).</summary>
    public TimeSpan OrphanGrace { get; set; } = TimeSpan.FromDays(1);
}

internal sealed class LeagueOptionsValidator : IValidateOptions<LeagueOptions>
{
    public ValidateOptionsResult Validate(string? name, LeagueOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = Problems(options).ToList();

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    /// <summary>Every broken rule at once, so a bad configuration is fixed in one go.</summary>
    internal static IEnumerable<string> Problems(LeagueOptions options)
    {
        var points = options.Points;
        var styleScore = options.StyleScore;
        var retention = options.Retention;

        static bool Positive(int value) => value >= 1;

        if (options.MinAge < 0)
        {
            yield return "League:MinAge must not be negative.";
        }

        if (!Positive(options.MaxPairsPerDay) || !Positive(options.VotesForPoints) || options.VotesForPoints > options.MaxPairsPerDay)
        {
            yield return "League:MaxPairsPerDay and VotesForPoints must be at least 1, and VotesForPoints at most MaxPairsPerDay.";
        }

        if (options.MinDecisionTime < TimeSpan.Zero || options.NewVoterVotes < 0 || options.NewVoterWeight is <= 0 or > 1)
        {
            yield return "League:MinDecisionTime and NewVoterVotes must not be negative; NewVoterWeight must be above 0 and at most 1.";
        }

        if (!Positive(options.MinGamesForWinRate) || !Positive(options.PairCandidates) || !Positive(options.MinComparisons))
        {
            yield return "League:MinGamesForWinRate, PairCandidates and MinComparisons must be at least 1.";
        }

        if (options.PriorWins <= 0 || options.PriorLosses <= 0 || options.BtTolerance <= 0 || !Positive(options.BtMaxIterations))
        {
            yield return "League:PriorWins, PriorLosses and BtTolerance must be above 0; BtMaxIterations at least 1.";
        }

        if (points.VotesMax < 0 || points.Voting < 0 || points.Worn < 0 || points.Piece < 0 || points.PieceDaysPerWeek is < 0 or > 7)
        {
            yield return "League:Points must not be negative, and PieceDaysPerWeek must be 0-7.";
        }

        if (options.BestDays is < 1 or > 7 || !Positive(options.CloseCatchUpDays))
        {
            yield return "League:BestDays must be 1-7 and CloseCatchUpDays at least 1.";
        }

        if (!Positive(options.GroupSize) || options.GroupMaxSize < options.GroupSize || options.PromoteCount < 0 || options.RelegateCount < 0
            || !Positive(options.MinTierPlayers) || !Positive(options.IdleWeeksToRelegate))
        {
            yield return "League:GroupSize, MinTierPlayers and IdleWeeksToRelegate must be at least 1, GroupMaxSize at least GroupSize, PromoteCount and RelegateCount not negative.";
        }

        if (!Positive(options.RepeatExactDays) || !Positive(options.RepeatNearDays) || options.RepeatNearMaxDifference < 0 || !Positive(options.MinPieces)
            || options.ModerationRetriesPerRun < 0)
        {
            yield return "League:RepeatExactDays, RepeatNearDays and MinPieces must be at least 1; RepeatNearMaxDifference and ModerationRetriesPerRun not negative.";
        }

        if (options.EngineFill.MinPool < 0 || options.EngineFill.MaxPerRun < 0)
        {
            yield return "League:EngineFill:MinPool and MaxPerRun must not be negative.";
        }

        if (!Positive(options.Reports.HideAt) || options.Reports.MinAccountAge < TimeSpan.Zero || options.Reports.MinCountedVotes < 0)
        {
            yield return "League:Reports:HideAt must be at least 1; MinAccountAge and MinCountedVotes must not be negative.";
        }

        if (!Positive(styleScore.WindowDays) || styleScore.MinRankedPerDay < 0 || !Positive(styleScore.MinDays) || styleScore.MinDays > styleScore.WindowDays
            || styleScore.Threshold is < 0 or > 100 || !Positive(styleScore.ConsecutiveWeeks))
        {
            yield return "League:StyleScore: WindowDays, MinDays and ConsecutiveWeeks at least 1, MinDays at most WindowDays, MinRankedPerDay not negative, Threshold 0-100.";
        }

        if (!Positive(retention.ImageDays) || !Positive(retention.PairDays) || retention.EntryDays < Math.Max(options.RepeatExactDays, styleScore.WindowDays)
            || retention.OrphanGrace < TimeSpan.FromHours(1))
        {
            yield return "League:Retention: ImageDays and PairDays at least 1; EntryDays at least RepeatExactDays and StyleScore:WindowDays (the rules read that far back); OrphanGrace at least an hour.";
        }
    }
}
