using System.Net;
using Kombin.Api.Common.Storage;
using Kombin.Api.Features.Wardrobe;

namespace Kombin.Api.Features.Catalog.Embeddings;

/// <summary>Settings of the <c>product-embeddings</c> job (section <c>CatalogEmbeddings</c>).</summary>
internal sealed class CatalogEmbeddingOptions
{
    public const string SectionName = "CatalogEmbeddings";

    /// <summary>Goes into the <c>User-Agent</c>, like the workers' <c>BOT_CONTACT</c>; the placeholder stays until the owner fills it.</summary>
    public string BotContact { get; set; } = "[YOUR EMAIL]";

    /// <summary>Time between two requests to one image host; 1 s like the workers. Tests set it to zero.</summary>
    public TimeSpan HostInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>How long one scheduled run may work before it stops (the rest is picked up by the next run).</summary>
    public TimeSpan MaxRunDuration { get; set; } = TimeSpan.FromHours(3);

    public string UserAgent => $"KombinBot/0.1 (+https://kombin.app/bot; contact: {BotContact})";
}

internal enum ImageFetchStatus
{
    Ok,

    /// <summary>404 or 410: the image is gone.</summary>
    NotFound,

    /// <summary>Not an accepted image, too large, or a URL we will not fetch: retrying will not help.</summary>
    Unusable,

    /// <summary>The host did not answer usefully (timeout, 5xx, 429): worth another try later.</summary>
    Transient,
}

internal sealed record ImageFetch(ImageFetchStatus Status, StoredObject? Image, string? Reason, TimeSpan? RetryAfter = null)
{
    public static ImageFetch Ok(StoredObject image) => new(ImageFetchStatus.Ok, image, null);
}

/// <summary>
/// Downloads catalog images for the embedding job: <c>KombinBot</c> user agent, one request per host per <see cref="CatalogEmbeddingOptions.HostInterval"/>
/// (<see cref="PoliteSchedule"/>), <c>Retry-After</c> honoured, at most the wardrobe photo limit (8 MB), image types only. Unlike the wardrobe's
/// copy (<c>CatalogImages</c>) it tells a missing image from a host that is down, because only the latter is worth retrying.
/// </summary>
internal sealed class CatalogImageFetcher(HttpClient http, PoliteSchedule schedule, TimeProvider time)
{
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromMinutes(2);

    public async Task<ImageFetch> FetchAsync(string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return new ImageFetch(ImageFetchStatus.Unusable, null, "image_bad_url");
        }

        var wait = schedule.Reserve(uri.Host, time.GetUtcNow());
        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait, time, cancellationToken);
        }

        try
        {
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var status = (int)response.StatusCode;

            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                return new ImageFetch(ImageFetchStatus.NotFound, null, $"image_{status}");
            }

            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            {
                var retryAfter = RetryAfter(response);
                schedule.PushBack(uri.Host, time.GetUtcNow(), retryAfter ?? schedule.Interval * 10);
                return new ImageFetch(ImageFetchStatus.Transient, null, $"image_{status}", retryAfter);
            }

            if (status >= 500 || response.StatusCode == HttpStatusCode.RequestTimeout)
            {
                return new ImageFetch(ImageFetchStatus.Transient, null, $"image_{status}");
            }

            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (!response.IsSuccessStatusCode)
            {
                return new ImageFetch(ImageFetchStatus.Unusable, null, $"image_{status}");
            }

            if (StorageKeys.ExtensionFor(contentType) is null)
            {
                return new ImageFetch(ImageFetchStatus.Unusable, null, "image_not_an_image");
            }

            if (response.Content.Headers.ContentLength > WardrobeLimits.MaxImageBytes)
            {
                return new ImageFetch(ImageFetchStatus.Unusable, null, "image_too_large");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;

            // The length header may be missing or wrong: stop reading at the limit either way.
            while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
            {
                if (buffer.Length + read > WardrobeLimits.MaxImageBytes)
                {
                    return new ImageFetch(ImageFetchStatus.Unusable, null, "image_too_large");
                }

                buffer.Write(chunk, 0, read);
            }

            return ImageFetch.Ok(new StoredObject(buffer.ToArray(), contentType!));
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return new ImageFetch(ImageFetchStatus.Transient, null, "image_unreachable");
        }
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        var delay = header?.Delta ?? (header?.Date is { } date ? date - DateTimeOffset.UtcNow : null);

        return delay is { } value && value > TimeSpan.Zero ? (value < MaxRetryAfter ? value : MaxRetryAfter) : null;
    }
}
