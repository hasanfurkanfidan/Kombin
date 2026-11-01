using Kombin.Api.Common.Results;
using Kombin.Api.Common.Validation;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Catalog;

namespace Kombin.Api.Features.Catalog.PutSeller;

/// <summary>The seller named in the route, validated on its own because the body does not carry it.</summary>
internal sealed record PutSellerRoute(string SellerSlug);

/// <summary>The whole seller: optional fields that are left out are cleared, as PUT means.</summary>
internal sealed record PutSellerRequest(
    string Name,
    string Kind,
    string WebsiteUrl,
    string? AffiliateNetwork,
    string? AffiliateUrlTemplate,
    Dictionary<string, string>? AffiliateParams,
    bool IsActive = true);

internal sealed record PutSellerResponse(
    Guid Id,
    string Slug,
    string Name,
    SellerKind Kind,
    string WebsiteUrl,
    string? AffiliateNetwork,
    string? AffiliateUrlTemplate,
    Dictionary<string, string>? AffiliateParams,
    bool IsActive,
    bool Created);

internal static class PutSellerEndpoint
{
    public static void Map(RouteGroupBuilder group) =>
        group.MapPut("/sellers/{sellerSlug}", Handle)
            .WithName("PutSeller")
            .WithValidation<PutSellerRoute>()
            .WithValidation<PutSellerRequest>()
            .Produces<PutSellerResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status409Conflict);

    /// <summary>Lets a worker register its seller (and its affiliate wrapping) before the first upsert instead of relying on the brand-store auto-create.</summary>
    private static async Task<IResult> Handle([AsParameters] PutSellerRoute route, PutSellerRequest request, KombinDbContext db, CancellationToken cancellationToken)
    {
        var slug = route.SellerSlug.Trim();
        var name = request.Name.Trim();

        if (await db.Sellers.AnyAsync(other => other.Name == name && other.Slug != slug, cancellationToken))
        {
            return CatalogErrors.SellerNameTaken.ToHttpResult();
        }

        var seller = await db.Sellers.SingleOrDefaultAsync(candidate => candidate.Slug == slug, cancellationToken);
        var created = seller is null;

        if (seller is null)
        {
            seller = new Seller { Slug = slug };
            db.Sellers.Add(seller);
        }

        seller.Name = name;
        seller.Kind = Enum.Parse<SellerKind>(request.Kind, ignoreCase: true);
        seller.WebsiteUrl = request.WebsiteUrl.Trim();
        seller.AffiliateNetwork = NullIfBlank(request.AffiliateNetwork);
        seller.AffiliateUrlTemplate = NullIfBlank(request.AffiliateUrlTemplate);
        seller.AffiliateParams = request.AffiliateParams is { Count: > 0 } parameters ? parameters : null;
        seller.IsActive = request.IsActive;

        await db.SaveChangesAsync(cancellationToken);

        return Results.Ok(new PutSellerResponse(
            seller.Id,
            seller.Slug,
            seller.Name,
            seller.Kind,
            seller.WebsiteUrl,
            seller.AffiliateNetwork,
            seller.AffiliateUrlTemplate,
            seller.AffiliateParams,
            seller.IsActive,
            created));
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
