using Kombin.Api.Features.Lens.Search;

namespace Kombin.Api.Features.Lens;

/// <summary>
/// "Görselden ürün bul": a photo in, the garments in it and the closest catalog products out. No tables of its own: it reads the catalog's
/// <c>product_embeddings</c> (filled by the Catalog's <c>product-embeddings</c> job) and the wardrobe's embeddings. Design:
/// <c>docs/design/visual-search.md</c>.
/// </summary>
internal static class LensModule
{
    public static IEndpointRouteBuilder MapLensEndpoints(this IEndpointRouteBuilder app)
    {
        // Public: an acquisition screen; a bearer token adds the wardrobe matches and the profile's audience.
        var lens = app.MapGroup("/api/v1/lens").WithTags("Lens");
        SearchEndpoint.Map(lens);

        return app;
    }
}
