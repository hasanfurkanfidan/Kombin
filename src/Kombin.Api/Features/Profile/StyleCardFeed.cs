using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Profile;

namespace Kombin.Api.Features.Profile;

internal static class StyleCardFeed
{
    /// <summary>The audiences of cards a profile with this audience gets: a Unisex profile sees everything, the others their own plus Unisex.</summary>
    public static bool IsVisibleTo(Audience profileAudience, Audience cardAudience) =>
        profileAudience == Audience.Unisex || cardAudience == profileAudience || cardAudience == Audience.Unisex;

    /// <summary>
    /// Orders the cards by <see cref="StyleCard.SortOrder"/>, then deals them out one family at a time (families in order of their
    /// first card), so any prefix of the result is as varied as the cards allow. The first n cards cover min(n, families) families.
    /// </summary>
    public static IReadOnlyList<StyleCard> Interleave(IEnumerable<StyleCard> cards)
    {
        var queues = cards
            .OrderBy(card => card.SortOrder)
            .ThenBy(card => card.Id)
            .GroupBy(card => card.Family)
            .Select(group => new Queue<StyleCard>(group))
            .ToList();

        var result = new List<StyleCard>();

        while (queues.Count > 0)
        {
            foreach (var queue in queues)
            {
                result.Add(queue.Dequeue());
            }

            queues.RemoveAll(queue => queue.Count == 0);
        }

        return result;
    }
}
