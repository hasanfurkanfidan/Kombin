using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Kombin.Api.Features.Wardrobe.Jobs;
using Kombin.Api.Persistence.Entities.Wardrobe;
using Microsoft.EntityFrameworkCore;
using Pgvector;

namespace Kombin.Api.IntegrationTests;

/// <summary>A bulk batch the test started: its ID, the text of the photo it uploaded (the fake answers by it) and the key the photo was stored under.</summary>
internal sealed record StartedBatch(Guid Id, string PhotoText, string OriginalKey);

internal static class BulkAddClientExtensions
{
    public const string Base = "/api/v1/me/wardrobe/bulk";

    public static Task<HttpResponseMessage> BulkUploadUrlRawAsync(this HttpClient client, string? contentType = "image/jpeg", long sizeBytes = 1024, CancellationToken cancellationToken = default) =>
        client.PostAsJsonAsync($"{Base}/upload-url", new { contentType, sizeBytes }, cancellationToken);

    public static Task<HttpResponseMessage> BulkProcessAsync(this HttpClient client, Guid batchId, CancellationToken cancellationToken = default) =>
        client.PostAsync($"{Base}/{batchId}/process", null, cancellationToken);

    public static Task<HttpResponseMessage> BulkStatusRawAsync(this HttpClient client, Guid batchId, CancellationToken cancellationToken = default) =>
        client.GetAsync($"{Base}/{batchId}", cancellationToken);

    public static async Task<JsonElement> BulkStatusAsync(this HttpClient client, Guid batchId, CancellationToken cancellationToken = default)
    {
        using var response = await client.BulkStatusRawAsync(batchId, cancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(cancellationToken));

        return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
    }

    /// <summary>Upload URL (asserts the 201) and the photo "PUT" into the fake store; the batch is still <c>Created</c>.</summary>
    public static async Task<StartedBatch> StartBulkAsync(this ApiFactory factory, HttpClient client, string flavor = "bulk-3", CancellationToken cancellationToken = default)
    {
        using var response = await client.BulkUploadUrlRawAsync(cancellationToken: cancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(cancellationToken));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var batchId = body.GetProperty("batchId").GetGuid();

        var photo = FakeVisionClient.Photo(flavor);
        var key = await factory.WithDbAsync(db => db.BulkBatches.Where(batch => batch.Id == batchId).Select(batch => batch.OriginalKey!).SingleAsync(cancellationToken));
        factory.Storage.Upload(key, photo, "image/jpeg");

        return new StartedBatch(batchId, Encoding.UTF8.GetString(photo), key);
    }

    /// <summary>Start, then <c>process</c> (asserts the 202); the analysis job is only recorded.</summary>
    public static async Task<StartedBatch> ProcessBulkAsync(this ApiFactory factory, HttpClient client, string flavor = "bulk-3", CancellationToken cancellationToken = default)
    {
        var started = await factory.StartBulkAsync(client, flavor, cancellationToken);
        using var process = await client.BulkProcessAsync(started.Id, cancellationToken);
        Assert.True(process.StatusCode == HttpStatusCode.Accepted, await process.Content.ReadAsStringAsync(cancellationToken));

        return started;
    }

    /// <summary>The whole path the app walks: upload URL, PUT, process, then the analysis job runs. Returns the batch.</summary>
    public static async Task<StartedBatch> RunBulkAsync(this ApiFactory factory, HttpClient client, string flavor = "bulk-3", CancellationToken cancellationToken = default)
    {
        var started = await factory.ProcessBulkAsync(client, flavor, cancellationToken);
        await factory.AnalyzeBulkAsync(started.Id, cancellationToken);

        return started;
    }

    public static Task<BulkAnalyzeOutcome> AnalyzeBulkAsync(this ApiFactory factory, Guid batchId, CancellationToken cancellationToken) =>
        factory.RunJobAsync<BulkAddAnalyzeJob, BulkAnalyzeOutcome>(job => job.AnalyzeAsync(batchId, cancellationToken));

    public static Task<BulkBatch> BulkBatchRowAsync(this ApiFactory factory, Guid batchId, CancellationToken cancellationToken) =>
        factory.WithDbAsync(db => db.BulkBatches.AsNoTracking().SingleAsync(batch => batch.Id == batchId, cancellationToken));

    public static Task<List<BulkPiece>> BulkPieceRowsAsync(this ApiFactory factory, Guid batchId, CancellationToken cancellationToken) =>
        factory.WithDbAsync(db => db.BulkPieces.AsNoTracking().Where(piece => piece.BatchId == batchId).OrderBy(piece => piece.SortOrder).ToListAsync(cancellationToken));

    /// <summary>Inserts a batch row directly (a state a test needs without walking the whole path).</summary>
    public static Task<Guid> InsertBulkBatchAsync(
        this ApiFactory factory,
        Guid userId,
        BulkBatchStatus status,
        DateTimeOffset expiresAt,
        DateTimeOffset? processRequestedAt = null,
        bool counted = false,
        CancellationToken cancellationToken = default) =>
        factory.WithDbAsync(async db =>
        {
            var batch = new BulkBatch { UserId = userId, Status = status, ExpiresAt = expiresAt, ProcessRequestedAt = processRequestedAt, Counted = counted };
            db.BulkBatches.Add(batch);
            await db.SaveChangesAsync(cancellationToken);

            return batch.Id;
        });

    /// <summary>A ready, not archived wardrobe item of <paramref name="userId"/> with this embedding and a thumbnail, as a bulk piece could look like it.</summary>
    public static Task<Guid> InsertItemWithEmbeddingAsync(
        this ApiFactory factory,
        Guid userId,
        float[] embedding,
        string? model = null,
        WardrobeItemStatus status = WardrobeItemStatus.Ready,
        bool archived = false,
        CancellationToken cancellationToken = default) =>
        factory.WithDbAsync(async db =>
        {
            var item = new WardrobeItem
            {
                UserId = userId,
                Status = status,
                IsArchived = archived,
                Embedding = new Vector(embedding),
                EmbeddingModel = model ?? FakeVisionClient.Model,
            };
            item.ImageThumbKey = $"wardrobe/{userId:D}/{item.Id:D}/thumb.webp";
            item.ImageCutoutKey = $"wardrobe/{userId:D}/{item.Id:D}/cutout.png";
            db.WardrobeItems.Add(item);
            await db.SaveChangesAsync(cancellationToken);

            return item.Id;
        });

    public static bool IsNull(this JsonElement element, string property) =>
        !element.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null;
}
