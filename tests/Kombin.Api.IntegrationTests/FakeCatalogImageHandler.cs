using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Kombin.Api.IntegrationTests;

/// <summary>
/// Answers the downloads of catalog images for <c>from-product</c>: a URL containing <c>missing</c> is a 404, anything else a small JPEG
/// whose bytes name the URL (see <see cref="BytesFor"/>), so a test can tell which image was copied.
/// </summary>
internal sealed class FakeCatalogImageHandler : HttpMessageHandler
{
    public static byte[] BytesFor(string url) => Encoding.UTF8.GetBytes($"catalog-image:{url}");

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();

        if (url.Contains("missing", StringComparison.Ordinal))
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        var content = new ByteArrayContent(BytesFor(url));
        content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
    }
}
