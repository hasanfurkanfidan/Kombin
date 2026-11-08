using Kombin.Api.Features.Catalog.Jobs;

namespace Kombin.Api.Features.Catalog.RunEmbeddings;

/// <summary><c>Limit</c>: how many due products to work on (default 100, at most 1000); <c>ProductIds</c> restricts the run to those products.</summary>
internal sealed record RunEmbeddingsRequest(int? Limit, IReadOnlyList<Guid>? ProductIds);

internal sealed record RunEmbeddingsResponse(int Due, int Embedded, int Reused, int Unusable, int Failed, int Remaining, bool Aborted);

internal static class RunEmbeddingsEndpoint
{
    public const int DefaultLimit = 100;

    public const int MaxLimit = 1000;

    /// <summary>
    /// <c>POST /internal/catalog/embeddings/run</c>: runs <see cref="ProductEmbeddingJob"/> on demand and waits for it, for a backfill or the compose
    /// walkthrough. The downloads keep the one-request-per-host-per-second pace, so 1000 products from one host take about 17 minutes; the
    /// nightly run has no limit, only a time budget. The body is optional.
    /// </summary>
    public static void Map(RouteGroupBuilder group) =>
        group.MapPost("/embeddings/run", Handle)
            .WithName("RunProductEmbeddings")
            .Produces<RunEmbeddingsResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

    private static async Task<IResult> Handle(RunEmbeddingsRequest? body, ProductEmbeddingJob job, CancellationToken cancellationToken)
    {
        var limit = Math.Clamp(body?.Limit ?? DefaultLimit, 1, MaxLimit);
        var summary = await job.EmbedAsync(body?.ProductIds, limit, budget: null, cancellationToken);

        return Results.Ok(new RunEmbeddingsResponse(
            summary.Due, summary.Embedded, summary.Reused, summary.Unusable, summary.Failed, summary.Remaining, summary.Aborted));
    }
}
