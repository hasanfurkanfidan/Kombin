using Kombin.Api.Persistence.Entities.Catalog;

namespace Kombin.Api.Features.Lens;

/// <summary>The coarse kind of garment a visual search stays within, so a photo of a coat never answers with sneakers.</summary>
internal enum CategoryFamily
{
    Top,
    Bottom,
    Outerwear,
    Shoes,
    Bag,
    Accessory,
}

/// <summary>A nearest neighbour from the index: <c>Distance</c> is the cosine distance (1 - similarity); <c>Price</c> the cheapest sellable offer's effective price, null when nothing can be bought.</summary>
internal sealed record LensCandidate(Guid ProductId, string SourceImageUrl, double Distance, decimal? Price)
{
    public double Similarity => 1 - Distance;

    public bool Sellable => Price is not null;
}

/// <summary>
/// The rules of "Görselden ürün bul", pure and unit-tested. Numbers come from the calibration in <c>docs/design/visual-search.md</c>
/// ("Quality guardrails"); change a constant there and here together.
/// <list type="number">
/// <item><b>Family.</b> A region searches only its category family (<see cref="FamilyOf"/>). A region whose category confidence is below
/// <see cref="LowConfidence"/> searches the families of all its candidates instead (widened, never dropped); a photo without regions
/// searches everything, with the stricter <see cref="WholePhotoFloor"/>.</item>
/// <item><b>Floor.</b> Nothing below <see cref="SimilarityFloor"/> (cosine similarity of unit CLIP vectors) is shown: "no result" is an
/// honest answer, a wrong one is embarrassing.</item>
/// <item><b>Order.</b> Similarity (to 6 decimals), minus <see cref="OutOfStockPenalty"/> for a product nobody sells right now (it stays,
/// behind its in-stock equals); then the cheaper (a product without a price last); then the product ID, so the order is stable.</item>
/// <item><b>Dedupe.</b> Two products with the same image (same URL, or the very same vector: identical distance) are one result; the first in
/// the order above (in stock, cheaper) stays.</item>
/// <item><b>Level.</b> <c>close</c> from <see cref="CloseMatch"/>, otherwise <c>similar</c>: the app shows "yakın eşleşme" / "benzer", never a
/// percentage.</item>
/// <item><b>Wardrobe.</b> The caller's own pieces from <see cref="WardrobeFloor"/>; <c>exact</c> from <see cref="WardrobeExact"/> or when the
/// piece came from a product in the results, otherwise <c>similar</c> (Social's vocabulary).</item>
/// </list>
/// </summary>
internal static class LensRanking
{
    /// <summary>
    /// Region crop vs catalog shot, within the family. Calibration: the photographed product itself scores ≥ 0.62 in 74 % of photos, the best
    /// unrelated product of another family only in 20 %; the family filter does the rest.
    /// </summary>
    public const double SimilarityFloor = 0.62;

    /// <summary>
    /// A whole photo (no region found) searches every family and shares the background with catalog shots, so it must be surer: at 0.78 the
    /// product itself is kept in 72 % of photos and a product of another family passes in 4 %.
    /// </summary>
    public const double WholePhotoFloor = 0.78;

    /// <summary>"Yakın eşleşme": above the best other product of the family in 93 % of photos, so it nearly always means the product itself.</summary>
    public const double CloseMatch = 0.72;

    /// <summary>Below this category confidence the family is widened to the region's candidates; the wardrobe's own category threshold.</summary>
    public const double LowConfidence = 0.35;

    public const double OutOfStockPenalty = 0.05;

    /// <summary>A piece of the caller's: their own garment scores ≥ 0.75 in 65 % of photos, the best other piece of its category in 35 %.</summary>
    public const double WardrobeFloor = 0.75;

    /// <summary>"Exact" by vector alone: above the best other piece of the category in more than 95 % of photos.</summary>
    public const double WardrobeExact = 0.88;

    /// <summary>Results per region.</summary>
    public const int MaxResults = 12;

    /// <summary>Neighbours read from the index per region, before the floor and the dedupe.</summary>
    public const int Candidates = 48;

    public const int MaxWardrobeMatches = 3;

    public const string Close = "close";

    public const string Similar = "similar";

    public static CategoryFamily? FamilyOf(string? categorySlug)
    {
        if (string.IsNullOrWhiteSpace(categorySlug))
        {
            return null;
        }

        return categorySlug switch
        {
            // Worn as a layer and photographed like a jacket (OutfitSlots has the same override).
            "ust-giyim/blazer" => CategoryFamily.Outerwear,
            "aksesuar/canta" => CategoryFamily.Bag,
            _ => categorySlug.Split('/')[0] switch
            {
                "ust-giyim" => CategoryFamily.Top,
                "alt-giyim" => CategoryFamily.Bottom,
                "dis-giyim" => CategoryFamily.Outerwear,
                "ayakkabi" => CategoryFamily.Shoes,
                "aksesuar" => CategoryFamily.Accessory,
                _ => null,
            },
        };
    }

    /// <summary>
    /// The families a region searches, or null for all of them (no category at all). <paramref name="candidates"/> are the region's category
    /// candidates, best first, the top one included.
    /// </summary>
    public static IReadOnlySet<CategoryFamily>? FamiliesFor(string? categorySlug, double confidence, IEnumerable<string> candidates)
    {
        if (FamilyOf(categorySlug) is not { } family)
        {
            return null;
        }

        if (confidence >= LowConfidence)
        {
            return new HashSet<CategoryFamily> { family };
        }

        var widened = new HashSet<CategoryFamily> { family };
        foreach (var candidate in candidates)
        {
            if (FamilyOf(candidate) is { } other)
            {
                widened.Add(other);
            }
        }

        return widened;
    }

    /// <summary>The audiences a filter lets through: a gender's own products and the unisex ones; Kids or Unisex alone.</summary>
    public static IReadOnlyList<Audience> AudiencesFor(Audience audience) => audience switch
    {
        Audience.Women => [Audience.Women, Audience.Unisex],
        Audience.Men => [Audience.Men, Audience.Unisex],
        _ => [audience],
    };

    /// <summary>Applies the floor, the order and the dedupe, and keeps at most <see cref="MaxResults"/>.</summary>
    public static List<LensCandidate> Rank(IEnumerable<LensCandidate> candidates, double floor)
    {
        var ordered = candidates
            .Where(candidate => candidate.Similarity >= floor)
            .OrderByDescending(candidate => Math.Round(candidate.Similarity - (candidate.Sellable ? 0 : OutOfStockPenalty), 6))
            .ThenBy(candidate => candidate.Price ?? decimal.MaxValue)
            .ThenBy(candidate => candidate.ProductId);

        var kept = new List<LensCandidate>();
        var images = new HashSet<string>(StringComparer.Ordinal);

        foreach (var candidate in ordered)
        {
            if (kept.Count == MaxResults)
            {
                break;
            }

            // The same pixels give the same vector, so the very same distance: a copied image under another URL.
            if (!images.Add(ImageKey(candidate.SourceImageUrl)) || kept.Any(other => Math.Abs(other.Distance - candidate.Distance) < 1e-7))
            {
                continue;
            }

            kept.Add(candidate);
        }

        return kept;
    }

    public static string Level(double similarity) => similarity >= CloseMatch ? Close : Similar;

    /// <summary><c>exact</c>, <c>similar</c> or null (not shown) for one of the caller's pieces.</summary>
    public static string? WardrobeMatch(double similarity, bool cameFromAResult) =>
        similarity < WardrobeFloor ? null : cameFromAResult || similarity >= WardrobeExact ? "exact" : "similar";

    /// <summary>Scheme and host are case-insensitive; the path and query are not (CDNs put the image identity there).</summary>
    public static string ImageKey(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? $"{uri.Scheme}://{uri.Host.ToLowerInvariant()}{(uri.IsDefaultPort ? string.Empty : $":{uri.Port}")}{uri.PathAndQuery}"
            : url;
}
