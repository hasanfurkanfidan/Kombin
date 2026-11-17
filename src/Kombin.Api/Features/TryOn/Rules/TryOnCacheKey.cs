using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Kombin.Api.Features.TryOn.Rules;

/// <summary>
/// One garment as it enters the cache key: its row, the row's answer (version, time and the source it answered) and the sizes and cut sent with it.
/// </summary>
internal sealed record CacheKeyGarment(Guid GarmentId, string? Version, DateTimeOffset? PreparedAt, string? PreparedHash, string? Size, string? ReferenceSize, string? Cut);

/// <summary>
/// The render cache key (try-on brief, "Cache key"), pure: SHA-256 (hex) of the renderer, the body values, each garment's row, version,
/// preparation time, answered source, size, reference size and cut in order, and the width. Every invalidation of the brief is a change of one of them, so nothing
/// has to be deleted for a stale render to stop being served: measurements or audience (the body), a size entry (the reference size), an item's
/// size, a product's photos or the service's version (the row is prepared again: version and time).
/// </summary>
internal static class TryOnCacheKey
{
    public static string For(string renderer, TryOnBody body, IReadOnlyList<CacheKeyGarment> garments, int width)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(garments);

        var text = new StringBuilder()
            .Append("renderer=").Append(renderer).Append('\n')
            .Append("body=").Append(body.Canonical()).Append('\n')
            .Append("width=").Append(width.ToString(CultureInfo.InvariantCulture)).Append('\n');

        foreach (var garment in garments)
        {
            text.Append("garment=")
                .Append(garment.GarmentId.ToString("N")).Append('|')
                .Append(garment.Version ?? "-").Append('|')
                .Append(garment.PreparedAt is { } at ? (at.UtcTicks / 10).ToString(CultureInfo.InvariantCulture) : "-").Append('|')
                .Append(garment.PreparedHash ?? "-").Append('|')
                .Append(garment.Size ?? "-").Append('|')
                .Append(garment.ReferenceSize ?? "-").Append('|')
                .Append(garment.Cut ?? "-").Append('\n');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
}
