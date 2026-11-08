using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Kombin.Api.Features.Wardrobe;

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

/// <summary>A value or an error, like <see cref="VisionResult"/> for the other endpoints.</summary>
internal sealed record VisionCall<T>(T? Value, string? Error, bool IsTransient)
    where T : class
{
    public static VisionCall<T> Ok(T value) => new(value, null, false);

    public static VisionCall<T> Permanent(string error) => new(null, error, false);

    public static VisionCall<T> Transient(string error) => new(null, error, true);
}

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
}

internal sealed partial class VisionClient(HttpClient http, ILogger<VisionClient> logger) : IVisionClient
{
    public const string KeyHeader = "X-Internal-Key";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // The model only changes when the vision service is redeployed with another one; asking once per process is enough.
    private static string? CachedEmbeddingModel;

    /// <summary>Sets the base address, key and timeout on the typed client.</summary>
    public static void Configure(IServiceProvider provider, HttpClient client)
    {
        var options = provider.GetRequiredService<IOptions<VisionOptions>>().Value;

        if (Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var baseUrl))
        {
            client.BaseAddress = baseUrl;
        }

        client.DefaultRequestHeaders.Add(KeyHeader, options.InternalKey);
        client.Timeout = TimeSpan.FromSeconds(30);
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

    /// <summary>Posts the image as the multipart field <c>image</c> and reads the JSON answer; failures become values, never exceptions.</summary>
    private async Task<VisionCall<T>> PostImageAsync<T>(string path, byte[] image, string contentType, CancellationToken cancellationToken)
        where T : class
    {
        if (http.BaseAddress is null)
        {
            return VisionCall<T>.Transient("vision_not_configured: set Vision:BaseUrl");
        }

        try
        {
            using var content = new MultipartFormDataContent();
            var file = new ByteArrayContent(image);
            file.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
            content.Add(file, "image", "upload");

            using var response = await http.PostAsync(new Uri(path, UriKind.Relative), content, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var error = $"vision_{(int)response.StatusCode}: {ReadDetail(body)}";
                LogCallFailed(logger, path, error);

                return IsTransient(response.StatusCode) ? VisionCall<T>.Transient(error) : VisionCall<T>.Permanent(error);
            }

            var value = JsonSerializer.Deserialize<T>(body, Json);

            return value is null ? VisionCall<T>.Transient("vision_empty_response") : VisionCall<T>.Ok(value);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException or IOException)
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
        if (CachedEmbeddingModel is not null || http.BaseAddress is null)
        {
            return CachedEmbeddingModel;
        }

        try
        {
            using var response = await http.GetAsync(new Uri("health", UriKind.Relative), cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (document.RootElement.TryGetProperty("clipModel", out var model) && model.ValueKind == JsonValueKind.String)
            {
                CachedEmbeddingModel = model.GetString();
            }

            return CachedEmbeddingModel;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException or IOException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return null;
        }
    }

    private static bool IsTransient(HttpStatusCode status) =>
        (int)status >= 500 || status is HttpStatusCode.Unauthorized or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;

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
