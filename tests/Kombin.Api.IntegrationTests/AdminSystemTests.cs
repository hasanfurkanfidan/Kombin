using System.Net;
using Kombin.Api.Common.Diagnostics;
using Kombin.Api.Common.Vision;
using Kombin.Api.Persistence.Entities.Admin;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kombin.Api.IntegrationTests;

/// <summary>
/// <c>/admin/system</c> ("Sistem durumu") and what feeds it: an unhandled error is counted under its route template and exception type (never the path,
/// never the message), the page shows it, and rows older than 30 days are deleted.
/// </summary>
public sealed class AdminSystemTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AnUnhandledErrorIsCountedUnderItsRouteTemplateAndTypeAndShownOnTheSystemPage()
    {
        // A host whose vision client throws: the system page itself (which asks vision for its health) becomes the failing route.
        await using var broken = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IVisionClient>();
            services.AddSingleton<IVisionClient, ThrowingVisionClient>();
        }));
        using (var brokenBrowser = new AdminBrowser(broken))
        {
            await brokenBrowser.SignInAsync(Ct);
            using var failed = await brokenBrowser.Client.GetAsync("/admin/system?email=ayse%40example.com", Ct);
            Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        }

        var row = await factory.WithDbAsync(db => db.ErrorEvents.AsNoTracking()
            .SingleAsync(error => error.ErrorCode == typeof(SystemPageTestException).FullName, Ct));
        Assert.Equal(ErrorLog.RequestKind, row.Kind);
        Assert.Equal("/admin/system", row.Source);

        using var browser = await factory.SignedInAdminAsync(Ct);
        // Decoded: the nested type's name has a '+', which Razor writes as an entity.
        var html = WebUtility.HtmlDecode(await browser.GetHtmlAsync("/admin/system", Ct));

        Assert.Contains("System status", html, StringComparison.Ordinal);
        Assert.Contains(typeof(SystemPageTestException).FullName!, html, StringComparison.Ordinal);
        Assert.Contains("<code>/admin/system</code>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("ayse", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(SystemPageTestException.Secret, html, StringComparison.Ordinal);
        Assert.Contains("Vision service", html, StringComparison.Ordinal);
        Assert.Contains("Last day closed", html, StringComparison.Ordinal);
        Assert.Contains("Workers, last delivery per seller", html, StringComparison.Ordinal);
        Assert.Contains("No <code>Sentry:Dsn</code>", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFinalJobFailureIsListedByJobNameWithItsLastErrorCode()
    {
        var job = "SystemTestJob" + Guid.NewGuid().ToString("N")[..8] + ".RunAsync";
        var errors = factory.Services.GetRequiredService<ErrorLog>();

        await errors.RecordAsync(ErrorLog.JobKind, job, "System.TimeoutException", Ct);
        await errors.RecordAsync(ErrorLog.JobKind, job, "System.Net.Http.HttpRequestException", Ct);

        using var browser = await factory.SignedInAdminAsync(Ct);
        var html = await browser.GetHtmlAsync("/admin/system", Ct);

        Assert.Contains($"<code>{job}</code>", html, StringComparison.Ordinal);
        Assert.Contains("System.Net.Http.HttpRequestException", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RowsOlderThanThirtyDaysAreDeletedOnTheNextWrite()
    {
        var source = "SystemPurgeTest" + Guid.NewGuid().ToString("N")[..8];
        var errors = new ErrorLog(factory.Services.GetRequiredService<IServiceScopeFactory>(), new TestClockAt(DateTimeOffset.UtcNow), NullLogger<ErrorLog>.Instance);
        await errors.RecordAsync(ErrorLog.JobKind, source, "Old", Ct);
        await factory.WithDbAsync(db => db.ErrorEvents.Where(error => error.Source == source)
            .ExecuteUpdateAsync(setters => setters.SetProperty(error => error.CreatedAt, DateTimeOffset.UtcNow.AddDays(-31)), Ct));

        // A new instance: the purge runs at most once an hour per instance.
        var next = new ErrorLog(factory.Services.GetRequiredService<IServiceScopeFactory>(), new TestClockAt(DateTimeOffset.UtcNow), NullLogger<ErrorLog>.Instance);
        await next.RecordAsync(ErrorLog.JobKind, source, "New", Ct);

        var left = await factory.WithDbAsync(db => db.ErrorEvents.AsNoTracking().Where(error => error.Source == source).Select(error => error.ErrorCode).ToListAsync(Ct));
        Assert.Equal(["New"], left);
    }

    private sealed class TestClockAt(DateTimeOffset now) : Common.Time.IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    private sealed class SystemPageTestException() : Exception(Secret)
    {
        public const string Secret = "message-with-ayse@example.com";
    }

    /// <summary>Only the health check is called by the system page; everything else is never reached.</summary>
    private sealed class ThrowingVisionClient : IVisionClient
    {
        public Task<VisionHealth> CheckHealthAsync(CancellationToken cancellationToken) => throw new SystemPageTestException();

        public Task<VisionResult> AnalyzeAsync(byte[] image, string contentType, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<VisionCall<VisionModeration>> ModerateAsync(byte[] image, string contentType, bool renditions, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<VisionCall<VisionDetection>> DetectAsync(byte[] image, string contentType, bool embed, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<VisionCall<VisionEmbedding>> EmbedAsync(byte[] image, string contentType, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<string?> GetEmbeddingModelAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<VisionCall<VisionPrepared>> PrepareAsync(IReadOnlyList<VisionImage> images, string categorySlug, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<VisionCall<VisionRender>> RenderAsync(string spec, IReadOnlyList<byte[]> garments, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<string?> GetRendererAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<VisionCall<VisionBulk>> BulkAsync(byte[] image, string contentType, TimeSpan timeout, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
