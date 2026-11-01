using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Profile;

namespace Kombin.Api.Features.Profile;

internal static class StyleCardOrdering
{
    /// <summary>
    /// Orders the swipe deck so that both the family and the audience rotate, with no randomness: the same cards always give the
    /// same order. Cards are first sorted by <see cref="StyleCard.SortOrder"/>, then dealt one at a time. Each step takes from the
    /// next audience in the rotation (Women, Men, Unisex for a Unisex profile; the profile's own audience, then Unisex, for the
    /// others) a card of the family used least so far (ties: the family whose first card comes first). If that audience has no card
    /// of the least-used families it is skipped for this step, so any prefix of the result covers min(n, families) families
    /// whenever the cards allow it. Nothing is dropped; audiences outside the rotation come last.
    /// </summary>
    public static IReadOnlyList<StyleCard> Order(IReadOnlyList<StyleCard> candidates, Audience profileAudience)
    {
        var sorted = candidates.OrderBy(card => card.SortOrder).ThenBy(card => card.Id).ToList();

        var familyRank = sorted
            .Select(card => card.Family)
            .Distinct()
            .Select((family, index) => (family, index))
            .ToDictionary(pair => pair.family, pair => pair.index);

        var rotation = RotationFor(profileAudience);

        var groups = sorted
            .GroupBy(card => card.Audience)
            .OrderBy(group => rotation.IndexOf(group.Key) is var index and >= 0 ? index : rotation.Count + (int)group.Key)
            .Select(group => group.ToList())
            .ToList();

        var used = familyRank.Keys.ToDictionary(family => family, _ => 0);
        var result = new List<StyleCard>(sorted.Count);
        var cursor = 0;

        while (result.Count < sorted.Count)
        {
            var leastUsed = groups.SelectMany(group => group).Min(card => used[card.Family]);

            for (var offset = 0; offset < groups.Count; offset++)
            {
                var position = (cursor + offset) % groups.Count;
                var pick = groups[position]
                    .Where(card => used[card.Family] == leastUsed)
                    .MinBy(card => familyRank[card.Family]);

                if (pick is null)
                {
                    continue;
                }

                groups[position].Remove(pick);
                used[pick.Family]++;
                result.Add(pick);
                cursor = position + 1;

                break;
            }
        }

        return result;
    }

    private static List<Audience> RotationFor(Audience profileAudience) => profileAudience switch
    {
        Audience.Women or Audience.Men => [profileAudience, Audience.Unisex],
        _ => [Audience.Women, Audience.Men, Audience.Unisex],
    };
}
