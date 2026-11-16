using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Kombin.Api.IntegrationTests;

/// <summary>
/// Answers the downloads of catalog images for <c>from-product</c> and the embedding job: a URL containing <c>missing</c> is a 404, <c>busy</c>
/// a 503, one containing <c>slow</c> answers after <see cref="SlowDelay"/>, anything else a small JPEG whose bytes name the URL (see <see cref="BytesFor"/>), so a test can tell which image was copied.
/// Every request is counted by URL (<see cref="Requests"/>), so a test can tell whether an image was fetched again.
/// </summary>
internal sealed class FakeCatalogImageHandler : HttpMessageHandler
{
    private static readonly ConcurrentDictionary<string, int> Counts = new(StringComparer.Ordinal);

    public static byte[] BytesFor(string url) => Encoding.UTF8.GetBytes($"catalog-image:{url}");

    /// <summary>The text of <see cref="BytesFor"/>, for <see cref="FakeVisionClient.LensPhoto(string, string)"/>.</summary>
    public static string TextFor(string url) => $"catalog-image:{url}";

    public static int Requests(string url) => Counts.GetValueOrDefault(url);

    private static readonly ConcurrentDictionary<string, ImageGate> Gates = new(StringComparer.Ordinal);

    /// <summary>
    /// Holds every request for exactly <paramref name="url"/> until <see cref="ImageGate.Open"/>: a run that is deterministically "in progress".
    /// <see cref="ImageGate.Entered"/> completes when a request has arrived, <see cref="ImageGate.Dispose"/> removes the gate.
    /// </summary>
    public static ImageGate Hold(string url)
    {
        var gate = new ImageGate(url);
        Gates[url] = gate;

        return gate;
    }

    internal sealed class ImageGate(string url) : IDisposable
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Open() => Release.TrySetResult();

        public void Dispose()
        {
            Release.TrySetResult();
            Gates.TryRemove(url, out _);
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();

        // A shop a test holds shut: the request waits until the test lets it go (or the run is cancelled).
        if (Gates.TryGetValue(url, out var gate))
        {
            gate.Entered.TrySetResult();
            await gate.Release.Task.WaitAsync(cancellationToken);
        }

        // A slow shop: the answer takes long enough for a second request to arrive meanwhile.
        if (url.Contains("slow", StringComparison.Ordinal))
        {
            await Task.Delay(SlowDelay, cancellationToken);
        }

        return Answer(request);
    }

    public static readonly TimeSpan SlowDelay = TimeSpan.FromMilliseconds(500);

    private static HttpResponseMessage Answer(HttpRequestMessage request)
    {
        var url = request.RequestUri!.ToString();
        Counts.AddOrUpdate(url, 1, (_, count) => count + 1);

        // ".../redirect?to=<escaped URL>" answers 302 to that URL, to test what a redirect may lead to.
        if (request.RequestUri.AbsolutePath.EndsWith("/redirect", StringComparison.Ordinal) && request.RequestUri.Query.StartsWith("?to=", StringComparison.Ordinal))
        {
            var redirect = new HttpResponseMessage(HttpStatusCode.Found);
            redirect.Headers.Location = new Uri(Uri.UnescapeDataString(request.RequestUri.Query[4..]));
            return redirect;
        }

        if (url.Contains("busy", StringComparison.Ordinal))
        {
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }

        if (url.Contains("missing", StringComparison.Ordinal))
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        var content = new ByteArrayContent(BytesFor(url));
        content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");

        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }
}
