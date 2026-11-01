using Kombin.Api.Common.Auth;
using Kombin.Api.Features.Catalog.GetPriceHistory;
using Kombin.Api.Features.Catalog.GetProduct;
using Kombin.Api.Features.Catalog.ListBrands;
using Kombin.Api.Features.Catalog.ListCategories;
using Kombin.Api.Features.Catalog.ListProducts;
using Kombin.Api.Features.Catalog.UpsertOffers;

namespace Kombin.Api.Features.Catalog;

internal static class CatalogModule
{
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

        return app;
    }
}
