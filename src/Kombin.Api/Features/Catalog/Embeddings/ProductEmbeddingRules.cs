using Kombin.Api.Persistence.Entities.Catalog;

namespace Kombin.Api.Features.Catalog.Embeddings;

/// <summary>What the job knows about a product's stored embedding row when it decides whether to (re)compute it.</summary>
internal sealed record EmbeddingState(ProductEmbeddingStatus Status, string Model, string SourceImageUrl, DateTimeOffset? NextAttemptAt, DateTimeOffset CheckedAt);

/// <summary>
/// When the <c>product-embeddings</c> job works on a product, and what a failure means. Pure, so the whole lifecycle is unit-tested:
/// <list type="bullet">
/// <item>no row → due (new products first);</item>
/// <item><c>Ready</c> → due again only when the image URL or the vision model changed;</item>
/// <item><c>Unusable</c> (404, not an image, refused) → due only when the URL changed or <see cref="UnusableRetryAfter"/> has passed, so a dead
/// image is not fetched on every run;</item>
/// <item><c>Failed</c> (transient) → due at <c>NextAttemptAt</c> (1 h, 2 h, 4 h ... capped at <see cref="MaxBackoff"/>), or when the URL changed.</item>
/// </list>
/// </summary>
internal static class ProductEmbeddingRules
{
    public static readonly TimeSpan UnusableRetryAfter = TimeSpan.FromDays(30);

    public static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(24);

    /// <summary>The run stops after this many vision failures in a row: the service is down, and every further attempt would only push products back.</summary>
    public const int MaxConsecutiveVisionFailures = 3;

    /// <summary>The image the product is embedded from: the primary image, else the first one; null when it has none.</summary>
    public static string? SourceImage(string? primaryImageUrl, string? firstImageUrl) =>
        string.IsNullOrWhiteSpace(primaryImageUrl) ? (string.IsNullOrWhiteSpace(firstImageUrl) ? null : firstImageUrl) : primaryImageUrl;

    public static bool IsDue(EmbeddingState? row, string imageUrl, string model, DateTimeOffset now)
    {
        if (row is null)
        {
            return true;
        }

        var urlChanged = !string.Equals(row.SourceImageUrl, imageUrl, StringComparison.Ordinal);

        return row.Status switch
        {
            ProductEmbeddingStatus.Ready => urlChanged || !string.Equals(row.Model, model, StringComparison.Ordinal),
            ProductEmbeddingStatus.Unusable => urlChanged || row.CheckedAt + UnusableRetryAfter <= now,
            _ => urlChanged || row.NextAttemptAt is null || row.NextAttemptAt <= now,
        };
    }

    /// <summary>When a product that failed <paramref name="attempts"/> times in a row is tried again: 1 h, 2 h, 4 h, ... at most a day.</summary>
    public static DateTimeOffset NextAttempt(int attempts, DateTimeOffset now)
    {
        var hours = Math.Pow(2, Math.Clamp(attempts - 1, 0, 10));

        return now + TimeSpan.FromHours(Math.Min(hours, MaxBackoff.TotalHours));
    }
}
