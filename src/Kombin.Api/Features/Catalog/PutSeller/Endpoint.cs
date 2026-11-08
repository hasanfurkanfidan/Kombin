using System.Text.Json.Serialization;
using Kombin.Api.Common.Results;
using Kombin.Api.Common.Validation;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Catalog;

namespace Kombin.Api.Features.Catalog.PutSeller;

/// <summary>The seller named in the route, validated on its own because the body does not carry it.</summary>
internal sealed record PutSellerRoute(string SellerSlug);

/// <summary>
/// The seller. <c>Name</c>, <c>Kind</c>, <c>WebsiteUrl</c> and <c>IsActive</c> are replaced on every PUT. The affiliate fields are the
/// exception, because workers that only know the shop would otherwise wipe what was configured by hand: left out = unchanged, an explicit
/// null (or blank) = cleared. The <c>*Set</c> flags record which of them the body contained; the JSON reader only calls an init accessor
/// for a property that is present.
/// </summary>
internal sealed record PutSellerRequest
{
    public string Name { get; init; } = string.Empty;

    public string Kind { get; init; } = string.Empty;

    public string WebsiteUrl { get; init; } = string.Empty;

    public string? AffiliateNetwork
    {
        get;
        init
        {
            field = value;
            AffiliateNetworkSet = true;
        }
    }

    public string? AffiliateUrlTemplate
    {
        get;
        init
        {
            field = value;
            AffiliateUrlTemplateSet = true;
        }
    }

    public Dictionary<string, string>? AffiliateParams
    {
        get;
        init
        {
            field = value;
            AffiliateParamsSet = true;
        }
    }

    public bool IsActive { get; init; } = true;

    [JsonIgnore]
    public bool AffiliateNetworkSet { get; private init; }

    [JsonIgnore]
    public bool AffiliateUrlTemplateSet { get; private init; }

    [JsonIgnore]
    public bool AffiliateParamsSet { get; private init; }
}

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
    string Country,
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
        seller.IsActive = request.IsActive;

        if (request.AffiliateNetworkSet)
        {
            seller.AffiliateNetwork = NullIfBlank(request.AffiliateNetwork);
        }

        if (request.AffiliateUrlTemplateSet)
        {
            seller.AffiliateUrlTemplate = NullIfBlank(request.AffiliateUrlTemplate);
        }

        if (request.AffiliateParamsSet)
        {
            seller.AffiliateParams = request.AffiliateParams is { Count: > 0 } parameters ? parameters : null;
        }

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
            seller.Country,
            created));
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
