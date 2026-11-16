using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.League;
using Kombin.Api.Persistence.Entities.Recommendation;

namespace Kombin.Api.Features.League.Rules;

/// <summary>An entry the voter may be shown: competing, not the voter's own, not reported by the voter, not hidden, its player in good standing.</summary>
internal sealed record PairCandidate(Guid Id, bool IsHuman, Audience Audience, Occasion? Occasion, int Exposures);

internal sealed record PairChoice(Guid LeftId, Guid RightId, LeaguePairMatch Level);

/// <summary>
/// The silent pairing (design note section 6). Pure and deterministic: ties are broken by a seeded hash of (voter, day, sequence, entry), so tests
/// can predict it and voters spread over equals.
/// <list type="number">
/// <item>A = the human candidate with the fewest exposures (a pair of two engine outfits teaches the ranking nothing);</item>
/// <item>B = the candidate with the fewest exposures, B ≠ A, (A, B) never served to this voter today, at the first level that has one:
/// <c>Strict</c> (same occasion, null only with null, and same audience), <c>SameAudience</c> (occasion dropped, "önce yer"), <c>Any</c>
/// (audience dropped too, "sonra kitle"). B may be human or engine;</item>
/// <item>if A has no B at any level, the next human; if none works, no pair;</item>
/// <item>left or right: a coin from the same hash.</item>
/// </list>
/// "Fewest" counts exposures (pairs served), not finished votes, so concurrent voters do not all receive the same least-voted outfit.
/// </summary>
internal static class Pairing
{
    private static readonly LeaguePairMatch[] Levels = [LeaguePairMatch.Strict, LeaguePairMatch.SameAudience, LeaguePairMatch.Any];

    public static PairChoice? Choose(
        IReadOnlyList<PairCandidate> candidates, IReadOnlySet<(Guid Low, Guid High)> served, Guid voterId, DateOnly day, int sequence)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(served);

        var ordered = candidates
            .Select(candidate => (Candidate: candidate, Tie: Tie(voterId, day, sequence, candidate.Id)))
            .OrderBy(item => item.Candidate.Exposures)
            .ThenBy(item => item.Tie)
            .ThenBy(item => item.Candidate.Id)
            .Select(item => item.Candidate)
            .ToList();

        foreach (var first in ordered.Where(candidate => candidate.IsHuman))
        {
            foreach (var level in Levels)
            {
                var second = ordered.FirstOrDefault(candidate =>
                    candidate.Id != first.Id && Matches(first, candidate, level) && !served.Contains(Key(first.Id, candidate.Id)));

                if (second is not null)
                {
                    var firstOnLeft = (Tie(voterId, day, sequence, Guid.Empty) & 1) == 0;

                    return firstOnLeft ? new PairChoice(first.Id, second.Id, level) : new PairChoice(second.Id, first.Id, level);
                }
            }
        }

        return null;
    }

    /// <summary>The two entry IDs ordered, as <c>league.pairs (low_entry_id, high_entry_id)</c> stores them.</summary>
    public static (Guid Low, Guid High) Key(Guid one, Guid other) => one.CompareTo(other) < 0 ? (one, other) : (other, one);

    public static bool Matches(PairCandidate first, PairCandidate second, LeaguePairMatch level)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);

        return level switch
        {
            LeaguePairMatch.Strict => first.Audience == second.Audience && first.Occasion == second.Occasion,
            LeaguePairMatch.SameAudience => first.Audience == second.Audience,
            _ => true,
        };
    }

    /// <summary>FNV-1a (64 bit) over the voter, the day, the sequence and the entry: stable across runs and machines.</summary>
    public static ulong Tie(Guid voterId, DateOnly day, int sequence, Guid entryId)
    {
        const ulong offset = 14695981039346656037;
        const ulong prime = 1099511628211;

        Span<byte> bytes = stackalloc byte[40];
        voterId.TryWriteBytes(bytes[..16]);
        BitConverter.TryWriteBytes(bytes.Slice(16, 4), day.DayNumber);
        BitConverter.TryWriteBytes(bytes.Slice(20, 4), sequence);
        entryId.TryWriteBytes(bytes[24..]);

        var hash = offset;
        foreach (var value in bytes)
        {
            hash = (hash ^ value) * prime;
        }

        return hash;
    }
}
