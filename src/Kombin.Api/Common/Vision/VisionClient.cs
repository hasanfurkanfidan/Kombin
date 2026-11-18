using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Kombin.Api.Common.Vision;

/// <summary>The <c>kombin-vision</c> service. <c>InternalKey</c> is that service's <c>VISION_INTERNAL_KEY</c>, not our own <c>Internal:ApiKey</c>.</summary>
internal sealed class VisionOptions
{
    public const string SectionName = "Vision";

    public string BaseUrl { get; set; } = string.Empty;

    public string InternalKey { get; set; } = string.Empty;
}

internal sealed record VisionCategory(string Slug, double Confidence);

/// <summary><c>Name</c> is a <c>ProductColor</c> name; <c>Hex</c> the measured shade.</summary>
internal sealed record VisionColor(string Name, double Confidence, string? Hex);

internal sealed record VisionPattern(string Name, double Confidence);

internal sealed record VisionQuality(bool IsGarment, double Confidence, IReadOnlyList<string> Warnings);

/// <summary>
/// The answer of <c>POST /v1/garment/analyze</c> (contract in <c>kombin-vision/README.md</c>). <c>Cutout</c> is a base64 PNG,
/// <c>Thumbnail</c> a base64 WEBP and <c>Embedding</c> 512 unit-length floats.
/// </summary>
internal sealed record VisionAnalysis(
    string Cutout,
    string Thumbnail,
    VisionCategory? Category,
    IReadOnlyList<VisionCategory> CategoryCandidates,
    VisionColor PrimaryColor,
    VisionColor? SecondaryColor,
    VisionPattern Pattern,
    float[] Embedding,
    VisionQuality Quality);

/// <summary>
/// Either an analysis or an error. <c>IsTransient</c> errors (network, timeout, 5xx, 401, 429) are worth retrying; the others (the
/// image itself was refused) are not.
/// </summary>
internal sealed record VisionResult(VisionAnalysis? Analysis, string? Error, bool IsTransient)
{
    public static VisionResult Ok(VisionAnalysis analysis) => new(analysis, null, false);

    public static VisionResult Permanent(string error) => new(null, error, false);

    public static VisionResult Transient(string error) => new(null, error, true);
}

/// <summary>Re-encoded images of a post photo (<c>/v1/moderate?renditions=true</c>): a base64 JPEG of at most 1600 px and a base64 WEBP preview.</summary>
internal sealed record VisionRenditions(string FeedImage, int FeedWidth, int FeedHeight, string Preview);

/// <summary>
/// The answer of <c>POST /v1/moderate</c>. <c>Verdict</c> is <c>allow</c>, <c>review</c> or <c>block</c>; <c>Labels</c> the labels at or above
/// their review threshold; <c>Scores</c> per label (<c>nsfw</c>, <c>nudity</c>, <c>violence</c>, <c>not_person_or_garment</c>).
/// </summary>
internal sealed record VisionModeration(
    bool Allowed,
    bool NeedsReview,
    string Verdict,
    IReadOnlyList<string> Labels,
    IReadOnlyDictionary<string, double> Scores,
    VisionRenditions? Renditions);

/// <summary>Fractions of the image (0-1).</summary>
internal sealed record VisionRegionBox(double X, double Y, double W, double H);

/// <summary>
/// A garment region proposal. <c>Slot</c> is an <c>OutfitSlot</c> name, the category a leaf slug of ours. <c>Embedding</c> (512 unit floats of
/// the region's cutout) is there only when the detection was asked with <c>embed</c>.
/// </summary>
internal sealed record VisionRegion(
    VisionRegionBox Box,
    string Slot,
    VisionCategory Category,
    IReadOnlyList<VisionCategory> CategoryCandidates,
    VisionColor Color,
    double Confidence,
    float[]? Embedding = null);

/// <summary>
/// The answer of <c>POST /v1/detect</c>: <c>Layout</c> is <c>person</c> or <c>flatlay</c>, at most 6 regions. <c>Embedding</c> is the whole
/// photo's, only with <c>embed</c>.
/// </summary>
internal sealed record VisionDetection(string Layout, IReadOnlyList<VisionRegion> Regions, float[]? Embedding = null);

/// <summary>The answer of <c>POST /v1/embed</c>: 512 unit-length floats.</summary>
internal sealed record VisionEmbedding(float[] Embedding);

/// <summary>
/// A value or an error, like <see cref="VisionResult"/> for the other endpoints. <c>Status</c> and <c>Detail</c> are the HTTP status and the
/// service's error code (<c>{"detail": "incomplete_outfit"}</c>) when it answered with an error; both are null for a network failure.
/// </summary>
internal sealed record VisionCall<T>(T? Value, string? Error, bool IsTransient)
    where T : class
{
    public int? Status { get; init; }

    public string? Detail { get; init; }

    /// <summary>The <c>Retry-After</c> header of an error answer (<c>503 busy</c>), as a delay; null when the service sent none or it could not be read.</summary>
    public TimeSpan? RetryAfter { get; init; }

    public static VisionCall<T> Ok(T value) => new(value, null, false);

    public static VisionCall<T> Permanent(string error) => new(null, error, false);

    public static VisionCall<T> Transient(string error) => new(null, error, true);

    /// <summary>An error answer of the service: transient or not as <see cref="VisionClient"/> classifies the status.</summary>
    public static VisionCall<T> Answered(int status, string detail, bool isTransient, TimeSpan? retryAfter = null) =>
        new(null, $"vision_{status}: {detail}", isTransient) { Status = status, Detail = detail, RetryAfter = retryAfter };
}

/// <summary>One photo for <c>/v1/garment/prepare</c>: its bytes and content type.</summary>
internal sealed record VisionImage(byte[] Content, string ContentType);

/// <summary>
/// The answer of <c>POST /v1/garment/prepare</c> (contract in <c>kombin-vision/README.md</c>). <c>Cutout</c> is a base64 RGBA PNG of the garment
/// only; <c>Landmarks</c> and <c>Sleeves</c> are kept as the service sent them (stored as jsonb and sent back to the renderer unchanged).
/// <c>MayBeOutermost</c> is false only for a top with sleeves folded across the front (or one the service cannot read).
/// </summary>
internal sealed record VisionPrepared(
    string Version,
    string Cutout,
    int Width,
    int Height,
    string Family,
    string Source,
    int ImageIndex,
    bool Reliable,
    IReadOnlyList<string> Flags,
    JsonElement Landmarks,
    JsonElement? Sleeves,
    bool? MayBeOutermost);

/// <summary>One garment of a <c>/v1/tryon/render</c> answer: <c>FitLevel</c> is <c>Tight</c>, <c>TrueToSize</c>, <c>Loose</c> or null.</summary>
internal sealed record VisionRenderedGarment(int Index, string? FitLevel, string? FitSource, string? Placement, IReadOnlyList<string>? Warnings);

/// <summary>The answer of <c>POST /v1/tryon/render</c>: a base64 image of <c>Width</c> × <c>Height</c>, one entry per garment sent.</summary>
internal sealed record VisionRender(
    string Renderer,
    string Image,
    string Format,
    int Width,
    int Height,
    IReadOnlyList<VisionRenderedGarment> Garments,
    IReadOnlyList<string> DefaultedMeasurements);

/// <summary>
/// One garment of a <c>/v1/garment/bulk</c> answer: its <c>Box</c> on the photo (fractions, like <c>/v1/detect</c>), whether the review screen starts
/// it ticked (<c>Reasons</c> is empty), the reason codes of a piece that is not, and <c>Analysis</c>, exactly the <c>/v1/garment/analyze</c> answer for
/// the piece's cutout (a piece added in bulk is the same wardrobe record as one added alone).
/// </summary>
internal sealed record VisionBulkPiece(VisionRegionBox Box, bool PreTicked, IReadOnlyList<string> Reasons, VisionAnalysis Analysis);

/// <summary>Garments the service saw together and did not cut apart: how many, and where (at least two per region).</summary>
internal sealed record VisionUnseparated(int Count, IReadOnlyList<VisionRegionBox> Regions);

/// <summary>
/// The answer of <c>POST /v1/garment/bulk</c> (contract in <c>kombin-vision/docs/design/bulk-add-briefs.md</c>). <c>Refusal</c> is one of seven codes
/// and then <c>Pieces</c> is empty; <c>StagesMs</c> are the service's own timings (<c>total</c> is the one the batch keeps).
/// </summary>
internal sealed record VisionBulk(
    string? Refusal,
    IReadOnlyList<VisionBulkPiece> Pieces,
    VisionUnseparated? Unseparated,
    IReadOnlyDictionary<string, double>? StagesMs);

/// <summary>What <c>GET /health</c> of the vision service said, for the admin panel: <c>Detail</c> is the HTTP status or why it could not be asked.</summary>
internal sealed record VisionHealth(bool IsHealthy, string? ClipModel, string Detail);

/// <summary>Never throws for a failed call (only when the caller cancels); the job decides what a failure means.</summary>
internal interface IVisionClient
{
    Task<VisionResult> AnalyzeAsync(byte[] image, string contentType, CancellationToken cancellationToken);

    /// <summary><c>POST /v1/moderate</c>, the safety filter for social posts; with <paramref name="renditions"/> also the feed image and preview.</summary>
    Task<VisionCall<VisionModeration>> ModerateAsync(byte[] image, string contentType, bool renditions, CancellationToken cancellationToken);

    /// <summary>
    /// <c>POST /v1/detect</c>, garment region proposals for an outfit photo; with <paramref name="embed"/> (visual search) every region and the
    /// whole photo also carry their embedding.
    /// </summary>
    Task<VisionCall<VisionDetection>> DetectAsync(byte[] image, string contentType, bool embed, CancellationToken cancellationToken);

    /// <summary><c>POST /v1/embed</c>: the embedding of the image as it is (no background removal), for catalog product shots.</summary>
    Task<VisionCall<VisionEmbedding>> EmbedAsync(byte[] image, string contentType, CancellationToken cancellationToken);

    /// <summary>The CLIP model behind the embeddings (<c>/health</c>'s <c>clipModel</c>), or null when the service cannot be asked.</summary>
    Task<string?> GetEmbeddingModelAsync(CancellationToken cancellationToken);

    /// <summary>A live <c>GET /health</c> (never cached, unlike the model name), for the admin panel's health strip.</summary>
    Task<VisionHealth> CheckHealthAsync(CancellationToken cancellationToken);

    /// <summary>
    /// <c>POST /v1/garment/prepare</c>: one to six photos of one garment (at most 8 MB each and 16 MB together; the caller scales them) and its
    /// leaf category slug. The service picks the photo and answers what the try-on renderer needs.
    /// </summary>
    Task<VisionCall<VisionPrepared>> PrepareAsync(IReadOnlyList<VisionImage> images, string categorySlug, CancellationToken cancellationToken);

    /// <summary>
    /// <c>POST /v1/tryon/render</c>: <paramref name="spec"/> is the JSON spec (body, garments, width, format), <paramref name="garments"/> the
    /// prepared cutouts in the same order.
    /// </summary>
    Task<VisionCall<VisionRender>> RenderAsync(string spec, IReadOnlyList<byte[]> garments, CancellationToken cancellationToken);

    /// <summary>The try-on renderer behind <c>/v1/tryon/render</c> (<c>/health</c>'s <c>renderer</c>), or null when the service cannot be asked.</summary>
    Task<string?> GetRendererAsync(CancellationToken cancellationToken);

    /// <summary>
    /// <c>POST /v1/garment/bulk</c>: one photo of several garments laid out → the pieces for a review screen (bulk add). The service takes ~6 s a
    /// photo and queues for up to 20 s, so <paramref name="timeout"/> is the caller's (the call is made from a background job); a busy service
    /// answers <c>503 busy</c> with <c>Retry-After</c> (<see cref="VisionCall{T}.RetryAfter"/>).
    /// </summary>
    Task<VisionCall<VisionBulk>> BulkAsync(byte[] image, string contentType, TimeSpan timeout, CancellationToken cancellationToken);
}

internal sealed partial class VisionClient(HttpClient http, ILogger<VisionClient> logger) : IVisionClient
{
    public const string KeyHeader = "X-Internal-Key";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // The model only changes when the vision service is redeployed with another one; asking once per process is enough.
    private static string? CachedEmbeddingModel;

    // Like the model: the renderer changes only with a redeploy of the vision service (restart the API after one, as for a new CLIP model).
    private static string? CachedRenderer;

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Sets the base address, key and timeout on the typed client.</summary>
    public static void Configure(IServiceProvider provider, HttpClient client)
    {
        var options = provider.GetRequiredService<IOptions<VisionOptions>>().Value;

        if (Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var baseUrl))
        {
            client.BaseAddress = baseUrl;
        }

        client.DefaultRequestHeaders.Add(KeyHeader, options.InternalKey);

        // Every call carries its own timeout (30 s unless the caller says otherwise: the bulk analysis waits longer), so the client's is only a ceiling.
        client.Timeout = Timeout.InfiniteTimeSpan;
    }

    public async Task<VisionResult> AnalyzeAsync(byte[] image, string contentType, CancellationToken cancellationToken)
    {
        var call = await PostImageAsync<VisionAnalysis>("v1/garment/analyze", image, contentType, cancellationToken);

        return call.Value is { } analysis
            ? VisionResult.Ok(analysis)
            : call.IsTransient ? VisionResult.Transient(call.Error!) : VisionResult.Permanent(call.Error!);
    }

    public Task<VisionCall<VisionModeration>> ModerateAsync(byte[] image, string contentType, bool renditions, CancellationToken cancellationToken) =>
        PostImageAsync<VisionModeration>(renditions ? "v1/moderate?renditions=true" : "v1/moderate", image, contentType, cancellationToken);

    public Task<VisionCall<VisionDetection>> DetectAsync(byte[] image, string contentType, bool embed, CancellationToken cancellationToken) =>
        PostImageAsync<VisionDetection>(embed ? "v1/detect?embed=true" : "v1/detect", image, contentType, cancellationToken);

    public Task<VisionCall<VisionEmbedding>> EmbedAsync(byte[] image, string contentType, CancellationToken cancellationToken) =>
        PostImageAsync<VisionEmbedding>("v1/embed?removeBackground=false", image, contentType, cancellationToken);

    public Task<VisionCall<VisionPrepared>> PrepareAsync(IReadOnlyList<VisionImage> images, string categorySlug, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(images);

        return PostAsync<VisionPrepared>("v1/garment/prepare", content =>
        {
            for (var index = 0; index < images.Count; index++)
            {
                var file = new ByteArrayContent(images[index].Content);
                file.Headers.ContentType = MediaTypeHeaderValue.Parse(images[index].ContentType);
                content.Add(file, "image", $"photo{index}");
            }

            content.Add(new StringContent(categorySlug), "category");
        }, cancellationToken);
    }

    public Task<VisionCall<VisionRender>> RenderAsync(string spec, IReadOnlyList<byte[]> garments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(garments);

        return PostAsync<VisionRender>("v1/tryon/render", content =>
        {
            content.Add(new StringContent(spec, System.Text.Encoding.UTF8, "application/json"), "spec");
            for (var index = 0; index < garments.Count; index++)
            {
                var file = new ByteArrayContent(garments[index]);
                file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
                content.Add(file, "garment", $"garment{index}.png");
            }
        }, cancellationToken);
    }

    public Task<VisionCall<VisionBulk>> BulkAsync(byte[] image, string contentType, TimeSpan timeout, CancellationToken cancellationToken) =>
        PostAsync<VisionBulk>("v1/garment/bulk", content =>
        {
            var file = new ByteArrayContent(image);
            file.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
            content.Add(file, "image", "upload");
        }, cancellationToken, timeout);

    /// <summary>Posts the image as the multipart field <c>image</c> and reads the JSON answer; failures become values, never exceptions.</summary>
    private Task<VisionCall<T>> PostImageAsync<T>(string path, byte[] image, string contentType, CancellationToken cancellationToken)
        where T : class =>
        PostAsync<T>(path, content =>
        {
            var file = new ByteArrayContent(image);
            file.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
            content.Add(file, "image", "upload");
        }, cancellationToken);

    /// <summary>
    /// Posts a multipart body built by <paramref name="build"/> and reads the JSON answer; failures become values, never exceptions. Only the
    /// path and the service's error code are logged: a render spec carries body measurements, which never reach a log line.
    /// </summary>
    private async Task<VisionCall<T>> PostAsync<T>(string path, Action<MultipartFormDataContent> build, CancellationToken cancellationToken, TimeSpan? timeout = null)
        where T : class
    {
        if (http.BaseAddress is null)
        {
            return VisionCall<T>.Transient("vision_not_configured: set Vision:BaseUrl");
        }

        try
        {
            using var content = new MultipartFormDataContent();
            build(content);

            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(timeout ?? DefaultTimeout);

            using var response = await http.PostAsync(new Uri(path, UriKind.Relative), content, limit.Token);
            var body = await response.Content.ReadAsStringAsync(limit.Token);

            if (!response.IsSuccessStatusCode)
            {
                var call = VisionCall<T>.Answered((int)response.StatusCode, ReadDetail(body), IsTransient(response.StatusCode), ReadRetryAfter(response));
                LogCallFailed(logger, path, call.Error!);

                return call;
            }

            var value = JsonSerializer.Deserialize<T>(body, Json);

            return value is null ? VisionCall<T>.Transient("vision_empty_response") : VisionCall<T>.Ok(value);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or OperationCanceledException or IOException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            LogCallFailed(logger, path, exception.Message);

            return VisionCall<T>.Transient($"vision_unreachable: {exception.Message}");
        }
    }

    public async Task<string?> GetEmbeddingModelAsync(CancellationToken cancellationToken)
    {
        if (CachedEmbeddingModel is null)
        {
            await ReadHealthAsync(cancellationToken);
        }

        return CachedEmbeddingModel;
    }

    public async Task<string?> GetRendererAsync(CancellationToken cancellationToken)
    {
        if (CachedRenderer is null)
        {
            await ReadHealthAsync(cancellationToken);
        }

        return CachedRenderer;
    }

    /// <summary>Reads <c>/health</c> once and caches its <c>clipModel</c> and <c>renderer</c> for the process; a failure caches nothing.</summary>
    private async Task ReadHealthAsync(CancellationToken cancellationToken)
    {
        if (http.BaseAddress is null)
        {
            return;
        }

        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(DefaultTimeout);

            using var response = await http.GetAsync(new Uri("health", UriKind.Relative), limit.Token);
            if (!response.IsSuccessStatusCode)
            {
                return;
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(limit.Token));
            if (document.RootElement.TryGetProperty("clipModel", out var model) && model.ValueKind == JsonValueKind.String)
            {
                CachedEmbeddingModel = model.GetString();
            }

            if (document.RootElement.TryGetProperty("renderer", out var renderer) && renderer.ValueKind == JsonValueKind.String)
            {
                CachedRenderer = renderer.GetString();
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or OperationCanceledException or IOException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
        }
    }

    public async Task<VisionHealth> CheckHealthAsync(CancellationToken cancellationToken)
    {
        if (http.BaseAddress is null)
        {
            return new VisionHealth(false, null, "Vision:BaseUrl is not set");
        }

        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(DefaultTimeout);

            using var response = await http.GetAsync(new Uri("health", UriKind.Relative), limit.Token);
            if (!response.IsSuccessStatusCode)
            {
                return new VisionHealth(false, null, $"HTTP {(int)response.StatusCode}");
            }

            string? model = null;
            try
            {
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(limit.Token));
                if (document.RootElement.TryGetProperty("clipModel", out var clip) && clip.ValueKind == JsonValueKind.String)
                {
                    model = clip.GetString();
                }
            }
            catch (JsonException)
            {
                // A healthy answer whose body is not the JSON we know is still a live service.
            }

            return new VisionHealth(true, model, "HTTP 200");
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or IOException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return new VisionHealth(false, null, $"unreachable: {exception.Message}");
        }
    }

    private static bool IsTransient(HttpStatusCode status) =>
        (int)status >= 500 || status is HttpStatusCode.Unauthorized or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;

    /// <summary><c>Retry-After</c> as a delay: delta seconds or an HTTP date (then the time from now, never negative); null when absent.</summary>
    private static TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta)
        {
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        }

        return header?.Date is { } date ? TimeSpan.FromTicks(Math.Max(0, (date - DateTimeOffset.UtcNow).Ticks)) : null;
    }

    /// <summary>The service answers errors as <c>{"detail": "unsupported_image"}</c>.</summary>
    private static string ReadDetail(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);

            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("detail", out var detail)
                && detail.ValueKind == JsonValueKind.String
                    ? detail.GetString()!
                    : Summarize(body);
        }
        catch (JsonException)
        {
            return Summarize(body);
        }
    }

    private static string Summarize(string text) => text.Length <= 200 ? text : text[..200];

    [LoggerMessage(Level = LogLevel.Warning, Message = "Vision call {Path} failed: {Reason}")]
    private static partial void LogCallFailed(ILogger logger, string path, string reason);
}
