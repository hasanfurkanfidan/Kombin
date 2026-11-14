using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Kombin.Api.Persistence.Entities.Admin;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Kombin.Api.IntegrationTests;

/// <summary>
/// A browser for the admin panel: a client with its own cookie jar and its own (fake) client address, so the login throttle of one test never
/// touches another. Redirects are not followed.
/// </summary>
internal sealed partial class AdminBrowser : IDisposable
{
    private static int NextAddress;

    public AdminBrowser(ApiFactory factory)
    {
        Client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

        // 10.x.y.z, unique per browser for the whole run.
        var number = Interlocked.Increment(ref NextAddress);
        Address = $"10.{(number >> 16) & 255}.{(number >> 8) & 255}.{number & 255}";
        Client.DefaultRequestHeaders.Add(TestRemoteIpStartupFilter.Header, Address);
    }

    public HttpClient Client { get; }

    public string Address { get; }

    public void Dispose() => Client.Dispose();

    public async Task<string> GetHtmlAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await Client.GetAsync(url, cancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"GET {url} answered {(int)response.StatusCode}");

        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    /// <summary>Loads <paramref name="pageUrl"/> to get a request verification token (and the antiforgery cookie), then posts <paramref name="fields"/> to <paramref name="postUrl"/>.</summary>
    public async Task<HttpResponseMessage> PostFormAsync(string pageUrl, string postUrl, IEnumerable<KeyValuePair<string, string>> fields, CancellationToken cancellationToken, bool withToken = true)
    {
        var data = fields.ToList();

        if (withToken)
        {
            data.Add(new KeyValuePair<string, string>("__AdminRequestVerificationToken", TokenIn(await GetHtmlAsync(pageUrl, cancellationToken))));
        }

        using var content = new FormUrlEncodedContent(data);

        return await Client.PostAsync(postUrl, content, cancellationToken);
    }

    public Task<HttpResponseMessage> LoginAsync(string email, string password, CancellationToken cancellationToken, bool withToken = true) =>
        PostFormAsync("/admin/login", "/admin/login", [new("Input.Email", email), new("Input.Password", password)], cancellationToken, withToken);

    public async Task SignInAsync(CancellationToken cancellationToken)
    {
        using var response = await LoginAsync(ApiFactory.AdminEmail, ApiFactory.AdminPassword, cancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/admin", response.Headers.Location?.OriginalString);
    }

    public static string TokenIn(string html)
    {
        var match = TokenPattern().Match(html);
        Assert.True(match.Success, "The page has no request verification token.");

        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    [GeneratedRegex("name=\"__AdminRequestVerificationToken\"[^>]*value=\"([^\"]+)\"")]
    private static partial Regex TokenPattern();
}

internal static class AdminClientExtensions
{
    public static async Task<AdminBrowser> SignedInAdminAsync(this ApiFactory factory, CancellationToken cancellationToken)
    {
        var browser = new AdminBrowser(factory);
        await browser.SignInAsync(cancellationToken);

        return browser;
    }

    public static Task<List<AdminAuditEntry>> AuditOfAsync(this ApiFactory factory, string action, string? targetId, CancellationToken cancellationToken) =>
        factory.WithDbAsync(db => db.AdminAuditLog.AsNoTracking()
            .Where(entry => entry.Action == action && entry.TargetId == targetId)
            .OrderBy(entry => entry.CreatedAt)
            .ToListAsync(cancellationToken));

    /// <summary>Sends a request with the internal key, whatever the verb.</summary>
    public static async Task<HttpResponseMessage> SendInternalAsync(this HttpClient client, HttpMethod method, string url, object? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Add("X-Internal-Key", ApiFactory.InternalApiKey);

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await client.SendAsync(request, cancellationToken);
    }

    /// <summary>Runs <paramref name="action"/> with a scoped service, the way a request would get it.</summary>
    public static async Task<TResult> WithServiceAsync<TService, TResult>(this ApiFactory factory, Func<TService, Task<TResult>> action)
        where TService : notnull
    {
        await using var scope = factory.Services.CreateAsyncScope();

        return await action(scope.ServiceProvider.GetRequiredService<TService>());
    }
}
