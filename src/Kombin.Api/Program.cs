using System.Text.Json.Serialization;
using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Health;
using Kombin.Api.Common.Http;
using Kombin.Api.Common.Storage;
using Kombin.Api.Common.Time;
using Kombin.Api.Common.Validation;
using Kombin.Api.Features.Analytics;
using Kombin.Api.Features.Catalog;
using Kombin.Api.Features.Engagement;
using Kombin.Api.Features.Identity;
using Kombin.Api.Features.Lens;
using Kombin.Api.Features.Notification;
using Kombin.Api.Features.Profile;
using Kombin.Api.Features.Recommendation;
using Kombin.Api.Features.Social;
using Kombin.Api.Features.Wardrobe;
using Kombin.Api.Persistence;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Serilog;
using Serilog.Events;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, logger) => logger
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext());

builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddKombinProblemDetails();
builder.Services.AddKombinAuth();
builder.Services.AddValidatorsFromAssembly(typeof(Program).Assembly);

// Enums travel as their names ("InStock", "BrandStore"), never as numbers.
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks().AddCheck<PostgresHealthCheck>("postgres");
builder.AddKombinPersistence();
builder.Services.AddIdentityFeature();
builder.Services.AddCatalogFeature();
builder.Services.AddEngagementFeature();
builder.Services.AddNotificationFeature();
builder.Services.AddAnalyticsFeature();
builder.Services.AddObjectStorage();
builder.Services.AddWardrobeFeature();
builder.Services.AddRecommendationFeature();
builder.Services.AddSocialFeature();

var app = builder.Build();

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseSerilogRequestLogging(options => options.GetLevel = RequestLogLevel);
app.UseExceptionHandler();
app.UseStatusCodePages();

app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// Liveness only proves the process answers; readiness (/health) also checks PostgreSQL.
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health");
app.MapIdentityEndpoints();
app.MapCatalogEndpoints();
app.MapEngagementEndpoints();
app.MapProfileEndpoints();
app.MapNotificationEndpoints();
app.MapAnalyticsEndpoints();
app.MapWardrobeEndpoints();
app.MapRecommendationEndpoints();
app.MapSocialEndpoints();
app.MapLensEndpoints();

await app.MigrateDatabaseOnStartupAsync();
await app.SeedStyleCardsOnStartupAsync();
await app.SeedDatabaseOnStartupAsync();

// Last on purpose: this is what first touches the Hangfire storage, so its schema is created after the EF migrations ran.
app.UseNotificationJobs();
app.UseCatalogJobs();
app.UseAnalyticsJobs();
app.UseWardrobeJobs();
app.UseSocialJobs();
await app.RunAsync();

static LogEventLevel RequestLogLevel(HttpContext context, double elapsedMilliseconds, Exception? exception)
{
    // The exception itself is logged once, at Error, by GlobalExceptionHandler; the request summary only warns.
    if (exception is not null)
    {
        return LogEventLevel.Error;
    }

    if (context.Response.StatusCode >= StatusCodes.Status500InternalServerError)
    {
        return LogEventLevel.Warning;
    }

    // Probes hit the health endpoints every few seconds; keep them out of the request log.
    return context.Request.Path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase)
        ? LogEventLevel.Verbose
        : LogEventLevel.Information;
}

/// <summary>Entry point; public only so <c>WebApplicationFactory&lt;Program&gt;</c> in the integration tests can reach it.</summary>
public partial class Program;
