using System.Security.Cryptography;
using System.Text;
using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Http;
using Kombin.Api.Common.Results;
using Kombin.Api.Common.Time;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Engagement;
using Microsoft.Extensions.Options;

namespace Kombin.Api.Features.Engagement.Redirect;

internal static class RedirectEndpoint
{
    private const int MaxUserAgentLength = 512;

    /// <summary>
    /// The only way the app sends a user to a seller: <c>GET /api/v1/go/{offerId}?src=&amp;personaId=</c>. It records a
    /// <see cref="Click"/> and answers 302 with the affiliate URL. Anonymous callers are fine; a valid bearer token adds the user.
    /// There is deliberately no rate limiting yet (out of scope, see CLAUDE.md); bots and scrapers will need it before launch.
    /// </summary>
    public static void Map(RouteGroupBuilder group) =>
        group.MapGet("/{offerId:guid}", Handle)
            .WithName("GoToOffer")
            .Produces(StatusCodes.Status302Found)
            .ProducesProblem(StatusCodes.Status404NotFound);

    private static async Task<IResult> Handle(
        Guid offerId,
        string? src,
        string? personaId,
        HttpContext httpContext,
        ICurrentUser currentUser,
        KombinDbContext db,
        IClock clock,
        IOptions<OutboundOptions> options,
        CancellationToken cancellationToken)
    {
        // Dead listings never get traffic: inactive offers and offers of switched-off sellers are as good as missing.
        var offer = await db.Offers
            .AsNoTracking()
            .Where(candidate => candidate.Id == offerId && candidate.IsActive && candidate.Seller.IsActive)
            .Select(candidate => new
            {
                candidate.Id,
                candidate.ProductId,
                candidate.SellerId,
                candidate.Url,
                candidate.Seller.AffiliateUrlTemplate,
                candidate.Seller.AffiliateParams,
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (offer is null)
        {
            return EngagementErrors.OfferNotAvailable.ToHttpResult();
        }

        var source = ClickSources.Normalize(src);

        // The click ID goes into the URL we redirect to, so it is reserved first and inserted explicitly: one append-only INSERT, no follow-up UPDATE.
        var clickId = await db.Database
            .SqlQuery<long>($"select nextval(pg_get_serial_sequence('engagement.clicks', 'id')) as \"Value\"")
            .SingleAsync(cancellationToken);

        var redirectUrl = AffiliateUrlBuilder.Build(offer.Url, offer.AffiliateUrlTemplate, offer.AffiliateParams, clickId, source);
        var userAgent = httpContext.Request.Headers.UserAgent.ToString();

        db.Clicks.Add(new Click
        {
            Id = clickId,
            UserId = currentUser.UserIdOrNull,
            OfferId = offer.Id,
            ProductId = offer.ProductId,
            SellerId = offer.SellerId,
            Source = source,
            PersonaId = Guid.TryParse(personaId, out var persona) && persona != Guid.Empty ? persona : null,
            CorrelationId = httpContext.GetCorrelationId() ?? Guid.NewGuid().ToString("N"),
            UserAgent = userAgent.Length == 0 ? null : userAgent[..Math.Min(userAgent.Length, MaxUserAgentLength)],
            IpHash = HashIp(httpContext.Connection.RemoteIpAddress?.ToString(), options.Value.IpSalt),
            RedirectUrl = redirectUrl,
            ClickedAt = clock.UtcNow,
        });
        await db.SaveChangesAsync(cancellationToken);

        // Each click must reach the handler: a cached redirect would skip recording it.
        httpContext.Response.Headers.CacheControl = "no-store";

        return Results.Redirect(redirectUrl);
    }

    /// <summary>Lower-case hex SHA-256 of the salt and the address, or null when the address is unknown. The raw IP is never stored.</summary>
    internal static string? HashIp(string? ipAddress, string salt) =>
        string.IsNullOrEmpty(ipAddress)
            ? null
            : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{salt}:{ipAddress}")));
}
