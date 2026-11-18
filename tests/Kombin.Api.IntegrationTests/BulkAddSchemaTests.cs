using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Wardrobe;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pgvector;

namespace Kombin.Api.IntegrationTests;

/// <summary><c>wardrobe.bulk_batches</c> and <c>wardrobe.bulk_pieces</c> (task BA2, PR 1): what is stored, what cascades and what the database refuses.</summary>
public sealed class BulkAddSchemaTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static float[] Unit(int hot)
    {
        var vector = new float[512];
        vector[hot] = 1f;

        return vector;
    }

    private static BulkBatch NewBatch(Guid userId) => new()
    {
        UserId = userId,
        Status = BulkBatchStatus.Ready,
        ExpiresAt = DateTimeOffset.UtcNow.AddHours(24),
    };

    private static BulkPiece NewPiece(BulkBatch batch, int order) => new()
    {
        BatchId = batch.Id,
        UserId = batch.UserId,
        SortOrder = order,
        BoxX = 0.07,
        BoxY = 0.48,
        BoxW = 0.46,
        BoxH = 0.4,
        PreTicked = order == 0,
        Reasons = order == 0 ? [] : ["cut_by_frame", "touching"],
        PrimaryColor = ProductColor.Beige,
        SecondaryColor = ProductColor.White,
        Pattern = ProductPattern.Solid,
        Formality = 2,
        Seasons = Season.Autumn | Season.Winter,
        Warnings = ["blurry"],
        Embedding = new Vector(Unit(order)),
        EmbeddingModel = FakeVisionClient.Model,
        AnalysisJson = """{"category":null}""",
        CutoutKey = $"bulk/{batch.UserId:D}/{batch.Id:D}/x/staged.png",
        ThumbKey = $"bulk/{batch.UserId:D}/{batch.Id:D}/x/stagedthumb.webp",
        SimilarItemId = Guid.NewGuid(),
    };

    [Fact]
    public async Task ABatchAndItsPiecesAreStoredAsTheyWere()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var batch = NewBatch(user.Id);
        batch.OriginalKey = $"bulk/{user.Id:D}/{batch.Id:D}/{batch.Id:D}/original.jpg";
        batch.UnseparatedCount = 2;
        batch.UnseparatedRegions = """[{"x":0.1,"y":0.2,"w":0.4,"h":0.3}]""";
        batch.CutOffCount = 1;
        batch.Refusal = null;
        batch.VisionMs = 5300;
        batch.ConfirmedItemIds = [Guid.NewGuid(), Guid.NewGuid()];
        var pieces = new[] { NewPiece(batch, 0), NewPiece(batch, 1) };

        await factory.WithDbAsync(async db =>
        {
            db.BulkBatches.Add(batch);
            db.BulkPieces.AddRange(pieces);
            await db.SaveChangesAsync(Ct);

            return 0;
        });

        var (stored, storedPieces) = await factory.WithDbAsync(async db => (
            await db.BulkBatches.AsNoTracking().SingleAsync(row => row.Id == batch.Id, Ct),
            await db.BulkPieces.AsNoTracking().Where(row => row.BatchId == batch.Id).OrderBy(row => row.SortOrder).ToListAsync(Ct)));

        Assert.Equal(BulkBatchStatus.Ready, stored.Status);
        Assert.Equal(batch.OriginalKey, stored.OriginalKey);
        Assert.Equal(2, stored.UnseparatedCount);
        Assert.Contains("0.4", stored.UnseparatedRegions, StringComparison.Ordinal);
        Assert.Equal(1, stored.CutOffCount);
        Assert.Equal(5300, stored.VisionMs);
        Assert.Equal(batch.ConfirmedItemIds, stored.ConfirmedItemIds);
        Assert.NotEqual(default, stored.CreatedAt);

        Assert.Equal(2, storedPieces.Count);
        var second = storedPieces[1];
        Assert.Equal(pieces[1].Id, second.Id);
        Assert.False(second.PreTicked);
        Assert.Equal(["cut_by_frame", "touching"], second.Reasons);
        Assert.Equal((ProductColor.Beige, ProductColor.White, ProductPattern.Solid), (second.PrimaryColor, second.SecondaryColor, second.Pattern));
        Assert.Equal((short)2, second.Formality);
        Assert.Equal(Season.Autumn | Season.Winter, second.Seasons);
        Assert.Equal(["blurry"], second.Warnings);
        Assert.Equal(Unit(1), second.Embedding!.ToArray());
        Assert.Equal(pieces[1].SimilarItemId, second.SimilarItemId);
        Assert.Equal((0.07, 0.48, 0.46, 0.4), (second.BoxX, second.BoxY, second.BoxW, second.BoxH));
    }

    [Fact]
    public async Task APiecesIdIsKeptAsTheJobGaveItBecauseItIsTheWardrobeItemsId()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var batch = NewBatch(user.Id);
        var itemId = Guid.CreateVersion7();
        var piece = new BulkPiece { Id = itemId, BatchId = batch.Id, UserId = user.Id };

        await factory.WithDbAsync(async db =>
        {
            db.BulkBatches.Add(batch);
            db.BulkPieces.Add(piece);
            await db.SaveChangesAsync(Ct);

            return 0;
        });

        Assert.True(await factory.WithDbAsync(db => db.BulkPieces.AnyAsync(row => row.Id == itemId, Ct)));
        Assert.Equal((short)3, await factory.WithDbAsync(db => db.BulkPieces.Where(row => row.Id == itemId).Select(row => row.Formality).SingleAsync(Ct)));
    }

    [Fact]
    public async Task DeletingTheBatchDeletesItsPiecesAndOnlyThose()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var gone = NewBatch(user.Id);
        var kept = NewBatch(user.Id);

        await factory.WithDbAsync(async db =>
        {
            db.BulkBatches.AddRange(gone, kept);
            db.BulkPieces.AddRange(NewPiece(gone, 0), NewPiece(gone, 1), NewPiece(kept, 0));
            await db.SaveChangesAsync(Ct);

            return 0;
        });

        await factory.WithDbAsync(db => db.BulkBatches.Where(row => row.Id == gone.Id).ExecuteDeleteAsync(Ct));

        Assert.Equal(0, await factory.WithDbAsync(db => db.BulkPieces.CountAsync(row => row.BatchId == gone.Id, Ct)));
        Assert.Equal(1, await factory.WithDbAsync(db => db.BulkPieces.CountAsync(row => row.BatchId == kept.Id, Ct)));
    }

    [Fact]
    public async Task DeletingTheUserDeletesEveryBatchAndPiece()
    {
        using var user = await factory.CreateUserAsync(Ct);
        using var bystander = await factory.CreateUserAsync(Ct);
        var batch = NewBatch(user.Id);
        var other = NewBatch(bystander.Id);

        await factory.WithDbAsync(async db =>
        {
            db.BulkBatches.AddRange(batch, other);
            db.BulkPieces.AddRange(NewPiece(batch, 0), NewPiece(other, 0));
            await db.SaveChangesAsync(Ct);

            return 0;
        });

        await factory.WithDbAsync(db => db.Users.Where(row => row.Id == user.Id).ExecuteDeleteAsync(Ct));

        Assert.Equal(0, await factory.WithDbAsync(db => db.BulkBatches.CountAsync(row => row.UserId == user.Id, Ct)));
        Assert.Equal(0, await factory.WithDbAsync(db => db.BulkPieces.CountAsync(row => row.UserId == user.Id, Ct)));
        Assert.Equal(1, await factory.WithDbAsync(db => db.BulkBatches.CountAsync(row => row.Id == other.Id, Ct)));
        Assert.Equal(1, await factory.WithDbAsync(db => db.BulkPieces.CountAsync(row => row.BatchId == other.Id, Ct)));
    }

    [Fact]
    public async Task TwoPiecesOfABatchNeverShareAPositionAndFormalityStaysInRange()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var batch = NewBatch(user.Id);
        await factory.WithDbAsync(async db =>
        {
            db.BulkBatches.Add(batch);
            db.BulkPieces.Add(NewPiece(batch, 0));
            await db.SaveChangesAsync(Ct);

            return 0;
        });

        var duplicate = await Assert.ThrowsAsync<DbUpdateException>(() => factory.WithDbAsync(async db =>
        {
            db.BulkPieces.Add(NewPiece(batch, 0));
            await db.SaveChangesAsync(Ct);

            return 0;
        }));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, ((PostgresException)duplicate.InnerException!).SqlState);

        var outOfRange = NewPiece(batch, 1);
        outOfRange.Formality = 6;
        var check = await Assert.ThrowsAsync<DbUpdateException>(() => factory.WithDbAsync(async db =>
        {
            db.BulkPieces.Add(outOfRange);
            await db.SaveChangesAsync(Ct);

            return 0;
        }));
        Assert.Equal(PostgresErrorCodes.CheckViolation, ((PostgresException)check.InnerException!).SqlState);
    }

    [Fact]
    public async Task ABatchNeedsAUserAndAPieceNeedsItsBatch()
    {
        var orphanBatch = NewBatch(Guid.NewGuid());
        var noUser = await Assert.ThrowsAsync<DbUpdateException>(() => factory.WithDbAsync(async db =>
        {
            db.BulkBatches.Add(orphanBatch);
            await db.SaveChangesAsync(Ct);

            return 0;
        }));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, ((PostgresException)noUser.InnerException!).SqlState);

        using var user = await factory.CreateUserAsync(Ct);
        var noBatch = await Assert.ThrowsAsync<DbUpdateException>(() => factory.WithDbAsync(async db =>
        {
            db.BulkPieces.Add(new BulkPiece { BatchId = Guid.NewGuid(), UserId = user.Id });
            await db.SaveChangesAsync(Ct);

            return 0;
        }));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, ((PostgresException)noBatch.InnerException!).SqlState);
    }

    [Fact]
    public async Task TheTablesLiveInTheWardrobeSchemaInSnakeCase()
    {
        var columns = await factory.WithDbAsync(db => db.Database.SqlQueryRaw<string>(
            "SELECT table_name || '.' || column_name AS \"Value\" FROM information_schema.columns WHERE table_schema = 'wardrobe' AND table_name IN ('bulk_batches', 'bulk_pieces')").ToListAsync(Ct));

        Assert.Contains("bulk_batches.cut_off_count", columns);
        Assert.Contains("bulk_batches.confirmed_item_ids", columns);
        Assert.Contains("bulk_batches.objects_deleted_at", columns);
        Assert.Contains("bulk_pieces.box_x", columns);
        Assert.Contains("bulk_pieces.similar_item_id", columns);
        Assert.Contains("bulk_pieces.embedding", columns);
        Assert.DoesNotContain(columns, column => column.Any(char.IsUpper));
    }
}
