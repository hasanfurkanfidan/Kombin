using System.Globalization;
using Kombin.Api.Features.Profile;
using Kombin.Api.Persistence.Entities.Catalog;

namespace Kombin.Api.Features.Notification;

/// <summary>A recently added product with what the match score and the notification text need.</summary>
internal sealed record WeeklyCandidate(Guid ProductId, string DisplayName, Audience Audience, ProductTraits Traits);

internal sealed record WeeklyPick(WeeklyCandidate Candidate, int Score);

/// <summary>The choice behind the weekly "new pieces that match you" notification, without any I/O.</summary>
internal static class WeeklyMatchSelection
{
    public const int MinScore = 70;

    public const int MaxPicks = 3;

    /// <summary>
    /// The best <see cref="MaxPicks"/> candidates for the audience scoring at least <see cref="MinScore"/>, highest score first; equal
    /// scores keep the order of <paramref name="candidates"/>, which is newest first.
    /// </summary>
    public static IReadOnlyList<WeeklyPick> Select(IEnumerable<WeeklyCandidate> candidates, Audience userAudience, Func<ProductTraits, int> score)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(score);

        return
        [
            .. candidates
                .Where(candidate => AudienceMatches(userAudience, candidate.Audience))
                .Select(candidate => new WeeklyPick(candidate, score(candidate.Traits)))
                .Where(pick => pick.Score >= MinScore)
                .OrderByDescending(pick => pick.Score)
                .Take(MaxPicks),
        ];
    }

    /// <summary>A Unisex shopper sees men's and women's pieces; a Women or Men shopper sees their own and unisex. Kids' products never.</summary>
    public static bool AudienceMatches(Audience userAudience, Audience productAudience) =>
        productAudience != Audience.Kids
        && (userAudience == Audience.Unisex || productAudience == Audience.Unisex || productAudience == userAudience);

    /// <summary>The ISO week of the Istanbul calendar date at <paramref name="now"/>, e.g. <c>2026-W44</c>; a new one starts on Monday.</summary>
    public static string IsoWeekKey(DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);

        var date = TimeZoneInfo.ConvertTime(now, zone).Date;

        return string.Create(CultureInfo.InvariantCulture, $"{ISOWeek.GetYear(date)}-W{ISOWeek.GetWeekOfYear(date):00}");
    }

    public static string DedupeKey(Guid personaId, string isoWeek) => $"newmatches:{personaId}:{isoWeek}";
}
