using System.Globalization;
using System.Text.Json;
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
/// its answer (the pieces' IDs, every one of them in the drawing) and the <c>Renderer</c> that drew it (<c>renderer</c> of the Ready answer).
/// </summary>
internal sealed record ReportTryOnRequest(Guid? OutfitId, Guid? ProductId, IReadOnlyList<Guid>? GarmentIds, string? Renderer);

internal static class ReportTryOnEndpoint
{
    public const int MaxGarments = 10;

    public static void Map(RouteGroupBuilder group) =>
        group.MapPost("/reports", Handle)
            .WithName("ReportTryOn")
            .WithDescription("\"Yanlış görünüyor\": a mannequin drawing looks wrong. It must be about one of the caller's own drawings of that outfit or product, by the renderer the Ready answer named (else 404 TryOn.RenderNotFound). Stored for review (a garment shown from the back, most often); one report per drawing, the same drawing again stores nothing.")
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

        // The report must be about one of the caller's own drawings of that subject, by the renderer that drew it: a made-up renderer name, an
        // outfit never drawn or someone else's drawing is not a report.
        var render = await FindRenderAsync(db, userId, request.Renderer!.Trim(), given, request.ProductId, allowed, cancellationToken);
        if (render is null)
        {
            return TryOnErrors.RenderNotFound.ToHttpResult();
        }

        // One report per user and drawing: a second one (other garments, a double tap) changes nothing.
        if (await db.TryOnReports.AnyAsync(report => report.UserId == userId && report.RenderId == render.Id, cancellationToken))
        {
            return TypedResults.NoContent();
        }

        db.TryOnReports.Add(new TryOnReport
        {
            UserId = userId,
            OutfitId = request.OutfitId,
            ProductId = request.ProductId,
            GarmentIds = [.. pieces.Select(piece => piece.Row!.Id).Distinct()],
            RenderId = render.Id,
            Renderer = render.Renderer,
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

    private sealed record Drawing(Guid Id, string Renderer);

    /// <summary>
    /// The caller's newest drawing (an image, not a refusal) by <paramref name="renderer"/> that holds every reported piece and belongs to the subject:
    /// for an outfit every piece it was drawn with is one of the outfit's, for a product it is drawn with the product.
    /// </summary>
    private static async Task<Drawing?> FindRenderAsync(
        KombinDbContext db, Guid userId, string renderer, IReadOnlyList<Guid> given, Guid? productId, IReadOnlyList<PieceRef> allowed, CancellationToken cancellationToken)
    {
        var candidates = db.TryOnRenders.AsNoTracking().Where(render => render.UserId == userId && render.ImageKey != null && render.Renderer == renderer);
        foreach (var id in given)
        {
            var needle = string.Create(CultureInfo.InvariantCulture, $"[{{\"id\":\"{id:D}\"}}]");
            candidates = candidates.Where(render => EF.Functions.JsonContains(render.GarmentsJson, needle));
        }

        var rows = await candidates
            .OrderByDescending(render => render.LastUsedAt)
            .Select(render => new { render.Id, render.Renderer, render.GarmentsJson })
            .ToListAsync(cancellationToken);

        var subject = allowed.Select(piece => piece.Id).ToHashSet();
        foreach (var row in rows)
        {
            var drawn = DrawnIds(row.GarmentsJson);
            if (productId is { } product ? drawn.Contains(product) : drawn.IsSubsetOf(subject))
            {
                return new Drawing(row.Id, row.Renderer);
            }
        }

        return null;
    }

    private static HashSet<Guid> DrawnIds(string garmentsJson)
    {
        using var document = JsonDocument.Parse(garmentsJson);

        return [.. document.RootElement.EnumerateArray().Select(garment => garment.GetProperty("id").GetGuid())];
    }
}
