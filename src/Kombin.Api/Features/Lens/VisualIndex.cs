using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Wardrobe;
using Pgvector;
using Pgvector.EntityFrameworkCore;

namespace Kombin.Api.Features.Lens;

internal sealed record WardrobeNeighbour(Guid ItemId, Guid? ProductId, double Distance)
{
    public double Similarity => 1 - Distance;
}

/// <summary>
/// The nearest-neighbour queries of visual search. Catalog: the HNSW index on <c>catalog.product_embeddings</c> (cosine distance), filtered
/// by family, audience, activity and model; call <see cref="TuneAsync"/> first, inside the request's transaction. Wardrobe: the caller's own
/// pieces, a few hundred rows at most, scanned exactly.
/// </summary>
internal static class VisualIndex
{
    /// <summary>
    /// Candidate list of the HNSW search (pgvector's default is 40). 100 keeps recall of the top 48 near 1 at 50k vectors for a few ms more;
    /// see the design doc ("Index").
    /// </summary>
    public const int EfSearch = 100;

    /// <summary>
    /// Search settings for this transaction only. <c>iterative_scan = relaxed_order</c> (pgvector 0.8) keeps scanning the graph while the
    /// filters reject rows, so a family that is a sixth of the catalog still fills its candidates; the slightly relaxed order does not matter,
    /// the results are re-ranked in memory.
    /// </summary>
    public static Task TuneAsync(KombinDbContext db, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlRawAsync(
            $"SET LOCAL hnsw.ef_search = {EfSearch}; SET LOCAL hnsw.iterative_scan = relaxed_order;", cancellationToken);

    /// <summary><paramref name="categoryIds"/> null: every category. <paramref name="audiences"/> null: every audience.</summary>
    public static async Task<List<(Guid ProductId, string SourceImageUrl, double Distance)>> NearestProductsAsync(
        KombinDbContext db,
        Vector query,
        string model,
        IReadOnlyCollection<Guid>? categoryIds,
        IReadOnlyCollection<Audience>? audiences,
        int limit,
        CancellationToken cancellationToken)
    {
        var embeddings = db.ProductEmbeddings.AsNoTracking()
            .Where(embedding => embedding.Embedding != null && embedding.Model == model && embedding.Product.IsActive);

        if (categoryIds is not null)
        {
            embeddings = embeddings.Where(embedding => categoryIds.Contains(embedding.Product.CategoryId));
        }

        if (audiences is not null)
        {
            embeddings = embeddings.Where(embedding => audiences.Contains(embedding.Product.Audience));
        }

        var rows = await embeddings
            .OrderBy(embedding => embedding.Embedding!.CosineDistance(query))
            .Take(limit)
            .Select(embedding => new { embedding.ProductId, embedding.SourceImageUrl, Distance = embedding.Embedding!.CosineDistance(query) })
            .ToListAsync(cancellationToken);

        return [.. rows.Select(row => (row.ProductId, row.SourceImageUrl, row.Distance))];
    }

    /// <summary>
    /// The caller's ready, kept pieces nearest first: photo pieces by their own embedding (same model), pieces added from the catalog (no
    /// photo, no embedding of their own) by their product's catalog embedding.
    /// </summary>
    public static async Task<List<WardrobeNeighbour>> NearestWardrobeAsync(
        KombinDbContext db, Guid userId, Vector query, string model, int limit, CancellationToken cancellationToken)
    {
        var kept = db.WardrobeItems.AsNoTracking()
            .Where(item => item.UserId == userId && !item.IsArchived && item.Status == WardrobeItemStatus.Ready);

        var photos = await kept
            .Where(item => item.Embedding != null && item.EmbeddingModel == model)
            .OrderBy(item => item.Embedding!.CosineDistance(query))
            .Take(limit)
            .Select(item => new { item.Id, item.ProductId, Distance = item.Embedding!.CosineDistance(query) })
            .ToListAsync(cancellationToken);

        var fromCatalog = await kept
            .Where(item => item.Embedding == null && item.ProductId != null)
            .Join(
                db.ProductEmbeddings.Where(embedding => embedding.Embedding != null && embedding.Model == model),
                item => item.ProductId,
                embedding => (Guid?)embedding.ProductId,
                (item, embedding) => new { item.Id, item.ProductId, Distance = embedding.Embedding!.CosineDistance(query) })
            .OrderBy(row => row.Distance)
            .Take(limit)
            .ToListAsync(cancellationToken);

        return
        [
            .. photos.Select(row => new WardrobeNeighbour(row.Id, row.ProductId, row.Distance))
                .Concat(fromCatalog.Select(row => new WardrobeNeighbour(row.Id, row.ProductId, row.Distance)))
                .OrderBy(neighbour => neighbour.Distance)
                .Take(limit),
        ];
    }
}
