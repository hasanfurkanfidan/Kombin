using Hangfire;
using Kombin.Api.Common.Auth;
using Kombin.Api.Features.Catalog.Embeddings;
using Kombin.Api.Features.Catalog.GetPriceHistory;
using Kombin.Api.Features.Catalog.GetProduct;
using Kombin.Api.Features.Catalog.Jobs;
using Kombin.Api.Features.Catalog.ListBrands;
using Kombin.Api.Features.Catalog.ListCategories;
using Kombin.Api.Features.Catalog.ListProducts;
using Kombin.Api.Features.Catalog.ListSellerOffers;
using Kombin.Api.Features.Catalog.PutSeller;
using Kombin.Api.Features.Catalog.RunEmbeddings;
using Kombin.Api.Features.Catalog.SweepOffers;
using Kombin.Api.Features.Catalog.UpsertOffers;
using Kombin.Api.Features.Notification;
using Microsoft.Extensions.Options;

namespace Kombin.Api.Features.Catalog;

internal static class CatalogModule
{
    public static IServiceCollection AddCatalogFeature(this IServiceCollection services)
    {
        services.AddScoped<OfferUpserter>();

        // A plain class with constructor injection, like the Notification jobs: Hangfire activates it from a scope, the tests resolve it directly.
        services.AddScoped<OfferSweepJob>();
        services.AddScoped<ProductEmbeddingJob>();

        services.AddOptions<CatalogEmbeddingOptions>().BindConfiguration(CatalogEmbeddingOptions.SectionName);
        services.AddHttpClient(ProductEmbeddingJob.ImageClient, (provider, client) =>
        {
            client.Timeout = TimeSpan.FromSeconds(20);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(provider.GetRequiredService<IOptions<CatalogEmbeddingOptions>>().Value.UserAgent);
        });

        return services;
    }

    public static IEndpointRouteBuilder MapCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        // Public: the mobile app browses before login.
        var catalog = app.MapGroup("/api/v1/catalog").WithTags("Catalog");
        ListCategoriesEndpoint.Map(catalog);
        ListBrandsEndpoint.Map(catalog);
        ListProductsEndpoint.Map(catalog);
        GetProductEndpoint.Map(catalog);
        GetPriceHistoryEndpoint.Map(catalog);

        // Internal: called by ingestion workers with X-Internal-Key.
        var internalCatalog = app.MapGroup("/api/v1/internal/catalog").WithTags("Catalog (internal)").RequireInternalKey();
        UpsertOffersEndpoint.Map(internalCatalog);
        SweepOffersEndpoint.Map(internalCatalog);
        ListSellerOffersEndpoint.Map(internalCatalog);
        PutSellerEndpoint.Map(internalCatalog);
        RunEmbeddingsEndpoint.Map(internalCatalog);

        return app;
    }

    /// <summary>
    /// Registers the recurring catalog jobs. Call it after <c>UseNotificationJobs</c> and the database migrations; like that method it does
    /// nothing unless <c>Hangfire:Enabled</c> is true, so tests and one-off tooling never touch the job tables.
    /// </summary>
    public static void UseCatalogJobs(this WebApplication app)
    {
        if (!app.Configuration.GetValue<bool>(NotificationModule.HangfireEnabledKey))
        {
            return;
        }

        app.Services.GetRequiredService<IRecurringJobManager>().AddOrUpdate<OfferSweepJob>(
            OfferSweepJob.JobId,
            job => job.RunAsync(CancellationToken.None),
            OfferSweepJob.Cron,
            new RecurringJobOptions { TimeZone = QuietHours.Zone });

        app.Services.GetRequiredService<IRecurringJobManager>().AddOrUpdate<ProductEmbeddingJob>(
            ProductEmbeddingJob.JobId,
            job => job.RunAsync(CancellationToken.None),
            ProductEmbeddingJob.Cron,
            new RecurringJobOptions { TimeZone = QuietHours.Zone });
    }
}
