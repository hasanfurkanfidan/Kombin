using System.Net;
using System.Net.Http.Headers;

namespace Kombin.Api.IntegrationTests;

/// <summary>Body limits: 10 MB by default, 1 MB for a JSON body, 8 MB (and its envelope) for the visual search photo.</summary>
public sealed class RequestLimitsTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ByteArrayContent Body(int bytes, string contentType) =>
        new(new byte[bytes]) { Headers = { ContentType = new MediaTypeHeaderValue(contentType) } };

    private async Task<HttpResponseMessage> PostAsync(string url, int bytes, string contentType, bool internalKey = false)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = Body(bytes, contentType) };

        if (internalKey)
        {
            request.Headers.Add("X-Internal-Key", ApiFactory.InternalApiKey);
        }

        return await client.SendAsync(request, Ct);
    }

    [Fact]
    public async Task AJsonBodyOverOneMegabyteIsRefusedBeforeTheHandlerReadsIt()
    {
        using var response = await PostAsync("/api/v1/events", 1024 * 1024 + 1, "application/json");

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        var problem = await response.ReadProblemAsync(Ct);
        Assert.Equal("Request.TooLarge", problem.GetProperty("errorCode").GetString());
        Assert.True(response.Headers.Contains("X-Correlation-ID"));
    }

    [Fact]
    public async Task AJsonBodyUnderTheLimitIsReadAsUsual()
    {
        // Not valid JSON, so it is a 400 of the handler's own, not a 413.
        using var response = await PostAsync("/api/v1/events", 2048, "application/json");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task TheInternalWorkerEndpointsKeepTheLargerLimitForTheirBatches()
    {
        using var response = await PostAsync("/api/v1/internal/catalog/offers/sweep", 2 * 1024 * 1024, "application/json", internalKey: true);

        Assert.NotEqual(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task AnyOtherBodyIsLimitedToTenMegabytes()
    {
        using var tooLarge = await PostAsync("/api/v1/events", (10 * 1024 * 1024) + 1, "application/octet-stream");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLarge.StatusCode);

        using var fine = await PostAsync("/api/v1/events", 3 * 1024 * 1024, "application/octet-stream");
        Assert.NotEqual(HttpStatusCode.RequestEntityTooLarge, fine.StatusCode);
    }

    [Fact]
    public async Task ThePhotoSearchTakesAnEightMegabytePhotoAndNothingBeyondItsEnvelope()
    {
        using var tooLarge = await PostAsync("/api/v1/lens/search", (8 * 1024 * 1024) + (128 * 1024), "multipart/form-data");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLarge.StatusCode);

        using var fine = await PostAsync("/api/v1/lens/search", 5 * 1024 * 1024, "application/octet-stream");
        Assert.NotEqual(HttpStatusCode.RequestEntityTooLarge, fine.StatusCode);
    }
}
