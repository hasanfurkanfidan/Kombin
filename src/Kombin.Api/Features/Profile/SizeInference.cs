using Kombin.Api.Persistence.Entities.Profile;

namespace Kombin.Api.Features.Profile;

/// <summary>A size the user has for the same garment group at another brand.</summary>
internal readonly record struct SizeVote(string Size, DateTimeOffset UpdatedAt);

/// <summary>A guess, not stored anywhere: what the user probably wears at a brand they have not told us about.</summary>
internal sealed record SizeSuggestion(string Size, SizeSource Source, decimal Confidence);

internal static class SizeInference
{
    public const int MinAgreeing = 2;

    private const decimal MaxConfidence = 0.8m;

    /// <summary>
    /// The most common size among the other brands' entries, provided at least <see cref="MinAgreeing"/> of them have it. A tie goes
    /// to the size entered most recently. Confidence is the share of entries that agree, never above 0.8: it is a guess.
    /// </summary>
    public static SizeSuggestion? Suggest(IReadOnlyCollection<SizeVote> otherBrands)
    {
        ArgumentNullException.ThrowIfNull(otherBrands);

        var best = otherBrands
            .GroupBy(vote => vote.Size, StringComparer.Ordinal)
            .Select(group => (Size: group.Key, Count: group.Count(), Latest: group.Max(vote => vote.UpdatedAt)))
            .Where(candidate => candidate.Count >= MinAgreeing)
            .OrderByDescending(candidate => candidate.Count)
            .ThenByDescending(candidate => candidate.Latest)
            .ThenBy(candidate => candidate.Size, StringComparer.Ordinal)
            .Cast<(string Size, int Count, DateTimeOffset Latest)?>()
            .FirstOrDefault();

        if (best is not { } winner)
        {
            return null;
        }

        var confidence = Math.Min(MaxConfidence, Math.Round((decimal)winner.Count / otherBrands.Count, 2));

        return new SizeSuggestion(winner.Size, SizeSource.Inferred, confidence);
    }
}
