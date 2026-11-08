using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Kombin.Api.IntegrationTests;

/// <summary>
/// Answers the downloads of catalog images for <c>from-product</c> and the embedding job: a URL containing <c>missing</c> is a 404, <c>busy</c>
/// a 503, anything else a small JPEG whose bytes name the URL (see <see cref="BytesFor"/>), so a test can tell which image was copied.
/// Every request is counted by URL (<see cref="Requests"/>), so a test can tell whether an image was fetched again.
/// </summary>
internal sealed class FakeCatalogImageHandler : HttpMessageHandler
{
    private static readonly ConcurrentDictionary<string, int> Counts = new(StringComparer.Ordinal);

    public static byte[] BytesFor(string url) => Encoding.UTF8.GetBytes($"catalog-image:{url}");

    /// <summary>The text of <see cref="BytesFor"/>, for <see cref="FakeVisionClient.LensPhoto(string, string)"/>.</summary>
    public static string TextFor(string url) => $"catalog-image:{url}";

    public static int Requests(string url) => Counts.GetValueOrDefault(url);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();
        Counts.AddOrUpdate(url, 1, (_, count) => count + 1);

        if (url.Contains("busy", StringComparison.Ordinal))
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }

        if (url.Contains("missing", StringComparison.Ordinal))
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        var content = new ByteArrayContent(BytesFor(url));
        content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
    }
}
