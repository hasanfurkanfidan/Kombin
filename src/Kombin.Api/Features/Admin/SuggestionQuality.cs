using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Recommendation;

namespace Kombin.Api.Features.Admin;

/// <summary>
/// How a group of suggested outfits fared. An outfit counts once, by where it ended up (<see cref="Outfit.Status"/>, which the feedback
/// endpoints keep current): <see cref="Liked"/> is saved or worn, <see cref="Dismissed"/> is "bana göre değil", and the rest got no answer.
/// </summary>
internal sealed record SuggestionQualityRow(string Label, int Suggested, int Liked, int Dismissed)
{
    /// <summary>Outfits the user answered at all.</summary>
    public int Answered => Liked + Dismissed;

    /// <summary>Liked ÷ answered: of the outfits the user reacted to, how many they wanted. Null when nothing was answered.</summary>
    public double? LikeRate => Rate(Liked, Answered);

    /// <summary>Liked ÷ suggested: how often a suggestion ends up saved or worn. Null when nothing was suggested.</summary>
    public double? LikedOfSuggested => Rate(Liked, Suggested);

    /// <summary>Dismissed ÷ suggested. Null when nothing was suggested.</summary>
    public double? DismissedOfSuggested => Rate(Dismissed, Suggested);

    internal static double? Rate(int part, int whole) => whole == 0 ? null : (double)part / whole;
}

/// <summary>One "bana göre değil" reason and its share of the dismissals that gave a reason.</summary>
internal sealed record SuggestionDismissReason(string Reason, int Count, double Share);

/// <summary>One day of the series, by the day the outfit was meant for (Istanbul).</summary>
internal sealed record SuggestionQualityDay(DateOnly Day, int Suggested, int Liked, int Dismissed);

/// <summary>Everything the "Öneri kalitesi" page shows for one window.</summary>
internal sealed record SuggestionQualityReport(
    DateTimeOffset From,
    DateTimeOffset To,
    SuggestionQualityRow Total,
    int Users,
    int Shuffles,
    int Saves,
    int Wears,
    int Dismisses,
    IReadOnlyList<SuggestionQualityRow> ByKind,
    IReadOnlyList<SuggestionQualityRow> ByOccasion,
    IReadOnlyList<SuggestionQualityRow> ByRule,
    IReadOnlyList<SuggestionQualityRow> BySlot,
    IReadOnlyList<SuggestionQualityRow> ByCompleteness,
    IReadOnlyList<SuggestionDismissReason> DismissReasons,
    IReadOnlyList<SuggestionQualityDay> Days);

/// <summary>
/// Reads what users did with the engine's outfits (<c>recommendation.outfits</c> and the append-only <c>recommendation.outfit_feedback</c>) for the
/// admin "Öneri kalitesi" page. Outfits are picked by when they were created (<c>[from, to)</c>); feedback counts (saves, wears, dismissals,
/// "başka göster") by when it was given. Every number is aggregated in PostgreSQL; nothing loads the rows themselves.
/// </summary>
internal static class SuggestionQuality
{
    /// <summary>Shown for an outfit without an occasion or a rule.</summary>
    public const string None = "(none)";

    public static async Task<SuggestionQualityReport> ReadAsync(KombinDbContext db, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        var outfits = db.Outfits.AsNoTracking().Where(outfit => outfit.CreatedAt >= from && outfit.CreatedAt < to);

        var byKind = await outfits
            .GroupBy(outfit => outfit.Kind)
            .Select(group => new Counts<OutfitKind>(
                group.Key,
                group.Count(),
                group.Count(outfit => outfit.Status == OutfitStatus.Saved || outfit.Status == OutfitStatus.Worn),
                group.Count(outfit => outfit.Status == OutfitStatus.Dismissed)))
            .ToListAsync(cancellationToken);

        var byOccasion = await outfits
            .GroupBy(outfit => outfit.Occasion)
            .Select(group => new Counts<Occasion?>(
                group.Key,
                group.Count(),
                group.Count(outfit => outfit.Status == OutfitStatus.Saved || outfit.Status == OutfitStatus.Worn),
                group.Count(outfit => outfit.Status == OutfitStatus.Dismissed)))
            .ToListAsync(cancellationToken);

        var byRule = await outfits
            .GroupBy(outfit => outfit.Rule)
            .Select(group => new Counts<string>(
                group.Key,
                group.Count(),
                group.Count(outfit => outfit.Status == OutfitStatus.Saved || outfit.Status == OutfitStatus.Worn),
                group.Count(outfit => outfit.Status == OutfitStatus.Dismissed)))
            .ToListAsync(cancellationToken);

        var byCompleteness = await outfits
            .GroupBy(outfit => outfit.Completeness)
            .Select(group => new Counts<OutfitCompleteness>(
                group.Key,
                group.Count(),
                group.Count(outfit => outfit.Status == OutfitStatus.Saved || outfit.Status == OutfitStatus.Worn),
                group.Count(outfit => outfit.Status == OutfitStatus.Dismissed)))
            .ToListAsync(cancellationToken);

        // An outfit counts once per slot it has, however many pieces fill that slot (two accessories are one accessory outfit).
        var slots = outfits.SelectMany(
            outfit => outfit.Items.Select(item => item.Slot).Distinct(),
            (outfit, slot) => new { Slot = slot, outfit.Status });
        var bySlot = await slots
            .GroupBy(row => row.Slot)
            .Select(group => new Counts<OutfitSlot>(
                group.Key,
                group.Count(),
                group.Count(row => row.Status == OutfitStatus.Saved || row.Status == OutfitStatus.Worn),
                group.Count(row => row.Status == OutfitStatus.Dismissed)))
            .ToListAsync(cancellationToken);

        var reasons = await outfits
            .Where(outfit => outfit.Status == OutfitStatus.Dismissed && outfit.DismissReason != null)
            .GroupBy(outfit => outfit.DismissReason!.Value)
            .Select(group => new { Reason = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);

        var days = await outfits
            .GroupBy(outfit => outfit.ForDate)
            .OrderBy(group => group.Key)
            .Select(group => new SuggestionQualityDay(
                group.Key,
                group.Count(),
                group.Count(outfit => outfit.Status == OutfitStatus.Saved || outfit.Status == OutfitStatus.Worn),
                group.Count(outfit => outfit.Status == OutfitStatus.Dismissed)))
            .ToListAsync(cancellationToken);

        var users = await outfits.Select(outfit => outfit.UserId).Distinct().CountAsync(cancellationToken);

        var actions = await db.OutfitFeedback.AsNoTracking()
            .Where(feedback => feedback.CreatedAt >= from && feedback.CreatedAt < to)
            .GroupBy(feedback => feedback.Action)
            .Select(group => new { Action = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);

        int ActionCount(OutfitFeedbackAction action) => actions.SingleOrDefault(row => row.Action == action)?.Count ?? 0;

        var reasonTotal = reasons.Sum(row => row.Count);
        var total = new SuggestionQualityRow("All", byKind.Sum(row => row.Suggested), byKind.Sum(row => row.Liked), byKind.Sum(row => row.Dismissed));

        return new SuggestionQualityReport(
            from,
            to,
            total,
            users,
            ActionCount(OutfitFeedbackAction.Shuffle),
            ActionCount(OutfitFeedbackAction.Save),
            ActionCount(OutfitFeedbackAction.Worn),
            ActionCount(OutfitFeedbackAction.Dismiss),
            Rows(byKind, kind => kind.ToString()),
            Rows(byOccasion, occasion => occasion?.ToString() ?? None),
            Rows(byRule, rule => string.IsNullOrEmpty(rule) ? None : rule),
            Rows(bySlot, slot => slot.ToString()),
            Rows(byCompleteness, completeness => completeness.ToString()),
            [
                .. reasons
                    .OrderByDescending(row => row.Count)
                    .ThenBy(row => row.Reason.ToString(), StringComparer.Ordinal)
                    .Select(row => new SuggestionDismissReason(row.Reason.ToString(), row.Count, (double)row.Count / reasonTotal)),
            ],
            days);
    }

    /// <summary>Most suggested first, then by label, so the page reads the same on every load.</summary>
    private static List<SuggestionQualityRow> Rows<TKey>(IEnumerable<Counts<TKey>> counts, Func<TKey, string> label) =>
    [
        .. counts
            .Select(row => new SuggestionQualityRow(label(row.Key), row.Suggested, row.Liked, row.Dismissed))
            .OrderByDescending(row => row.Suggested)
            .ThenBy(row => row.Label, StringComparer.Ordinal),
    ];

    private sealed record Counts<TKey>(TKey Key, int Suggested, int Liked, int Dismissed);
}
