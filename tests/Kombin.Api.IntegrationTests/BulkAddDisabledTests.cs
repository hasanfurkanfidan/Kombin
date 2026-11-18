using System.Net;
using System.Net.Http.Json;
using Kombin.Api.Features.Wardrobe.BulkAdd;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Kombin.Api.IntegrationTests;

/// <summary>"Toplu ekle" is off by default (task BA2, PR 1): every route answers 404 <c>Wardrobe.BulkDisabled</c>, and a bad setting refuses the start.</summary>
public sealed class BulkAddDisabledTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Guid SomeBatch = Guid.NewGuid();

    private static readonly (HttpMethod Method, string Path)[] Routes =
    [
        (HttpMethod.Post, "/api/v1/me/wardrobe/bulk/upload-url"),
        (HttpMethod.Post, $"/api/v1/me/wardrobe/bulk/{SomeBatch}/process"),
        (HttpMethod.Get, $"/api/v1/me/wardrobe/bulk/{SomeBatch}"),
        (HttpMethod.Post, $"/api/v1/me/wardrobe/bulk/{SomeBatch}/confirm"),
        (HttpMethod.Delete, $"/api/v1/me/wardrobe/bulk/{SomeBatch}"),
    ];

    private static HttpRequestMessage Request((HttpMethod Method, string Path) route) =>
        new(route.Method, route.Path) { Content = route.Method == HttpMethod.Get || route.Method == HttpMethod.Delete ? null : JsonContent.Create(new { }) };

    [Fact]
    public void TheFlagIsOffByDefaultInTheTestHost() =>
        Assert.False(factory.Services.GetRequiredService<IOptions<BulkAddOptions>>().Value.Enabled);

    [Fact]
    public void EveryBulkRouteIsMappedAndTheListAboveIsComplete()
    {
        var mapped = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText!.StartsWith("/api/v1/me/wardrobe/bulk", StringComparison.Ordinal))
            .SelectMany(endpoint => (endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["*"]).Select(method => $"{method} {endpoint.RoutePattern.RawText}"))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
            [
                "DELETE /api/v1/me/wardrobe/bulk/{id:guid}",
                "GET /api/v1/me/wardrobe/bulk/{id:guid}",
                "POST /api/v1/me/wardrobe/bulk/upload-url",
                "POST /api/v1/me/wardrobe/bulk/{id:guid}/confirm",
                "POST /api/v1/me/wardrobe/bulk/{id:guid}/process",
            ],
            mapped);
        Assert.Equal(Routes.Length, mapped.Count);
    }

    [Fact]
    public async Task EveryRouteAnswers404BulkDisabledToASignedInUser()
    {
        using var user = await factory.CreateUserAsync(Ct);

        foreach (var route in Routes)
        {
            using var request = Request(route);
            using var response = await user.Client.SendAsync(request, Ct);

            Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{route.Method} {route.Path}: {(int)response.StatusCode}");
            var problem = await response.ReadProblemAsync(Ct);
            Assert.Equal("Wardrobe.BulkDisabled", problem.GetProperty("errorCode").GetString());
            Assert.True(response.Headers.Contains("X-Correlation-ID"));
        }
    }

    [Fact]
    public async Task ASignInIsStillNeededFirst()
    {
        using var anonymous = factory.CreateAnonymousClient();

        foreach (var route in Routes)
        {
            using var request = Request(route);
            using var response = await anonymous.SendAsync(request, Ct);

            Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, $"{route.Method} {route.Path}: {(int)response.StatusCode}");
        }
    }

    [Fact]
    public async Task SwitchedOnTheFilterLetsTheRoutesThrough()
    {
        // PR 1 has no behaviour yet: with the flag on the routes answer the placeholder, never the disabled answer. PR 2 and 3 replace it.
        await using var on = factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection([new KeyValuePair<string, string?>("BulkAdd:Enabled", "true")])));
        using var user = await factory.CreateUserAsync(Ct);
        using var client = on.CreateClient();
        client.DefaultRequestHeaders.Authorization = user.Client.DefaultRequestHeaders.Authorization;

        foreach (var route in Routes)
        {
            using var request = Request(route);
            using var response = await client.SendAsync(request, Ct);

            Assert.True(response.StatusCode == HttpStatusCode.ServiceUnavailable, $"{route.Method} {route.Path}: {(int)response.StatusCode}");
            Assert.Equal("Wardrobe.BulkNotImplemented", (await response.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString());
        }
    }

    [Theory]
    [InlineData("BulkAdd:MaxPiecesPerPhoto", "0")]
    [InlineData("BulkAdd:MaxPiecesPerPhoto", "13")]
    [InlineData("BulkAdd:MaxPhotosPerDay", "0")]
    [InlineData("BulkAdd:DuplicateSimilarity", "2")]
    [InlineData("BulkAdd:VisionTimeout", "00:10:00")]
    public void ABadSettingRefusesTheStartEvenWithTheFlagOff(string key, string value)
    {
        using var bad = factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection([new KeyValuePair<string, string?>(key, value)])));

        var failure = Assert.Throws<OptionsValidationException>(() => bad.CreateClient());
        Assert.Contains(key, string.Join(' ', failure.Failures), StringComparison.Ordinal);
    }
}
