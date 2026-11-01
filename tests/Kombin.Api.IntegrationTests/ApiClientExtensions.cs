using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Kombin.Api.IntegrationTests;

internal sealed record TokenPair(string AccessToken, string RefreshToken, int ExpiresIn);

internal static class ApiClientExtensions
{
    public const string DefaultPassword = "Passw0rd-ok";

    public static string NewEmail() => $"user-{Guid.NewGuid():N}@example.com";

    public static Task<HttpResponseMessage> RegisterAsync(
        this HttpClient client, string email, string password = DefaultPassword, string displayName = "Test User", CancellationToken cancellationToken = default) =>
        client.PostAsJsonAsync("/api/v1/auth/register", new { email, password, displayName }, cancellationToken);

    public static Task<HttpResponseMessage> LoginAsync(
        this HttpClient client, string email, string password, string? deviceName = null, CancellationToken cancellationToken = default) =>
        client.PostAsJsonAsync("/api/v1/auth/login", new { email, password, deviceName }, cancellationToken);

    public static Task<HttpResponseMessage> RefreshAsync(this HttpClient client, string refreshToken, CancellationToken cancellationToken = default) =>
        client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken }, cancellationToken);

    public static async Task<HttpResponseMessage> LogoutAsync(
        this HttpClient client, string? accessToken, string refreshToken, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout")
        {
            Content = JsonContent.Create(new { refreshToken }),
        };

        if (accessToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        return await client.SendAsync(request, cancellationToken);
    }

    public static async Task<HttpResponseMessage> GetMeAsync(
        this HttpClient client, string? accessToken, string? correlationId = null, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/me");

        if (accessToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        if (correlationId is not null)
        {
            request.Headers.Add("X-Correlation-ID", correlationId);
        }

        return await client.SendAsync(request, cancellationToken);
    }

    public static async Task<TokenPair> ReadTokensAsync(this HttpResponseMessage response, CancellationToken cancellationToken = default) =>
        await response.Content.ReadFromJsonAsync<TokenPair>(cancellationToken)
        ?? throw new InvalidOperationException("The response did not contain a token pair.");

    /// <summary>Reads an <c>application/problem+json</c> body, failing if the content type is anything else.</summary>
    public static async Task<JsonElement> ReadProblemAsync(this HttpResponseMessage response, CancellationToken cancellationToken = default)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
    }

    public static async Task<TokenPair> RegisterAndReadAsync(this HttpClient client, string? email = null, CancellationToken cancellationToken = default)
    {
        using var response = await client.RegisterAsync(email ?? NewEmail(), cancellationToken: cancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.Created, response.StatusCode);
        return await response.ReadTokensAsync(cancellationToken);
    }
}
