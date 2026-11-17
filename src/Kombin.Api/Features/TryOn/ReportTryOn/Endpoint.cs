using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Results;
using Kombin.Api.Common.Time;
using Kombin.Api.Common.Validation;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.TryOn;
using Npgsql;

namespace Kombin.Api.Features.TryOn.ReportTryOn;

/// <summary>
/// "Yanlış görünüyor": exactly one of <c>OutfitId</c> and <c>ProductId</c> (what <c>GET /me/tryon</c> was asked with), the <c>GarmentIds</c> of
/// its answer (the pieces' IDs) and the <c>Renderer</c> that drew it.
/// </summary>
internal sealed record ReportTryOnRequest(Guid? OutfitId, Guid? ProductId, IReadOnlyList<Guid>? GarmentIds, string? Renderer);

internal static class ReportTryOnEndpoint
{
    public const int MaxGarments = 10;

    public static void Map(RouteGroupBuilder group) =>
        group.MapPost("/reports", Handle)
            .WithName("ReportTryOn")
            .WithDescription("\"Yanlış görünüyor\": a mannequin drawing looks wrong. Stored for review (a garment shown from the back, most often); the same report twice is stored once.")
            .WithValidation<ReportTryOnRequest>()
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

    private static async Task<IResult> Handle(ReportTryOnRequest request, ICurrentUser currentUser, KombinDbContext db, IClock clock, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId;
        var given = request.GarmentIds!.Distinct().ToList();

        // The pieces the subject allows: the outfit's (the caller's own outfit only), or the product and the caller's own wardrobe items.
        List<PieceRef> allowed;
        if (request.OutfitId is { } outfitId)
        {
            allowed = await db.OutfitItems.AsNoTracking()
                .Where(item => item.OutfitId == outfitId && db.Outfits.Any(outfit => outfit.Id == outfitId && outfit.UserId == userId))
                .Select(item => new PieceRef(item.WardrobeItemId, item.ProductId))
                .ToListAsync(cancellationToken);
            if (allowed.Count == 0)
            {
                return TryOnErrors.OutfitNotFound.ToHttpResult();
            }
        }
        else
        {
            var productId = request.ProductId!.Value;
            if (!await db.Products.AnyAsync(product => product.Id == productId, cancellationToken))
            {
                return TryOnErrors.ProductNotFound.ToHttpResult();
            }

            allowed = await db.WardrobeItems.AsNoTracking()
                .Where(item => item.UserId == userId && given.Contains(item.Id))
                .Select(item => new PieceRef(item.Id, null))
                .ToListAsync(cancellationToken);
            allowed.Add(new PieceRef(null, productId));
        }

        var refs = given.Select(id => allowed.FirstOrDefault(piece => piece.Id == id)).ToList();
        if (refs.Any(piece => piece is null))
        {
            return TryOnErrors.UnknownGarment.ToHttpResult();
        }

        var pieces = await TryOnPieces.LoadAsync(db, userId, refs!, cancellationToken);
        if (pieces.Any(piece => piece.Row is null))
        {
            return TryOnErrors.UnknownGarment.ToHttpResult();
        }

        var renderer = request.Renderer!.Trim();
        var exists = await db.TryOnReports.AnyAsync(
            report => report.UserId == userId && report.OutfitId == request.OutfitId && report.ProductId == request.ProductId && report.Renderer == renderer,
            cancellationToken);
        if (exists)
        {
            return TypedResults.NoContent();
        }

        db.TryOnReports.Add(new TryOnReport
        {
            UserId = userId,
            OutfitId = request.OutfitId,
            ProductId = request.ProductId,
            GarmentIds = [.. pieces.Select(piece => piece.Row!.Id).Distinct()],
            Renderer = renderer,
            CreatedAt = clock.UtcNow,
        });

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // A double tap: the first one is stored.
        }

        return TypedResults.NoContent();
    }
}
