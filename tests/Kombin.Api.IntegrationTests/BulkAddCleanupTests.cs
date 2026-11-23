using System.Net;
using Kombin.Api.Common.Storage;
using Kombin.Api.Features.Wardrobe.Jobs;
using Kombin.Api.Persistence.Entities.Wardrobe;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Kombin.Api.IntegrationTests;

/// <summary>
/// <c>bulk-add-cleanup</c> (<c>docs/design/bulk-add.md</c>, Cleanup): unfinished batches past <c>expires_at</c> become <c>Expired</c> (pieces and
/// objects gone), a <c>Processing</c> batch older than 15 minutes becomes <c>Failed</c> <c>Interrupted</c>, tombstones go after 7 days, objects under
/// <c>bulk/{user}/</c> without a batch go, and all of it also with the flag off. Every run names the test's user and its own time.
/// </summary>
public sealed class BulkAddCleanupTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private List<string> KeysUnder(Guid userId, string prefix) => [.. factory.Storage.KeysOfUser(userId).Where(key => key.StartsWith(prefix, StringComparison.Ordinal))];

    private async Task<(TestUser User, StartedBatch Batch, DateTimeOffset Now)> ReadyAsync(string flavor = "bulk-3")
    {
        var user = await factory.CreateUserAsync(Ct);
        var now = BulkAddClientExtensions.PinnedNow();
        user.Client.At(now);

        return (user, await factory.RunBulkAsync(user.Client, flavor, Ct), now);
    }

    // ---- 24 h ----

    [Fact]
    public async Task AReadyBatchPastItsExpiryEndsAsExpired()
    {
        var (user, batch, now) = await ReadyAsync();
        using var _ = user;

        var summary = await factory.CleanupBulkAsync(user.Id, now.AddHours(24).AddSeconds(1), Ct);

        Assert.Equal(1, summary.Expired);
        var row = await factory.BulkBatchRowAsync(batch.Id, Ct);
        Assert.Equal(BulkBatchStatus.Expired, row.Status);
        Assert.Equal(now.AddHours(24).AddSeconds(1), row.EndedAt);
    }

    [Fact]
    public async Task AnExpiredBatchLosesItsPieces()
    {
        var (user, batch, now) = await ReadyAsync();
        using var _ = user;

        await factory.CleanupBulkAsync(user.Id, now.AddHours(25), Ct);

        Assert.Empty(await factory.BulkPieceRowsAsync(batch.Id, Ct));
    }

    [Fact]
    public async Task AnExpiredBatchLosesItsObjects()
    {
        var (user, batch, now) = await ReadyAsync();
        using var _ = user;
        var prefix = StorageKeys.BulkBatchPrefix(user.Id, batch.Id);
        Assert.NotEmpty(KeysUnder(user.Id, prefix));

        await factory.CleanupBulkAsync(user.Id, now.AddHours(25), Ct);

        Assert.Empty(KeysUnder(user.Id, prefix));
        Assert.NotNull((await factory.BulkBatchRowAsync(batch.Id, Ct)).ObjectsDeletedAt);
    }

    [Fact]
    public async Task ABatchBeforeItsExpiryIsLeftAlone()
    {
        var (user, batch, now) = await ReadyAsync();
        using var _ = user;

        var summary = await factory.CleanupBulkAsync(user.Id, now.AddHours(23).AddMinutes(59), Ct);

        Assert.Equal(0, summary.Expired);
        Assert.Equal(BulkBatchStatus.Ready, (await factory.BulkBatchRowAsync(batch.Id, Ct)).Status);
        Assert.Equal(3, (await factory.BulkPieceRowsAsync(batch.Id, Ct)).Count);
        Assert.NotEmpty(KeysUnder(user.Id, StorageKeys.BulkBatchPrefix(user.Id, batch.Id)));
    }

    [Fact]
    public async Task AFailedBatchPastItsExpiryLosesItsPhoto()
    {
        var (user, batch, now) = await ReadyAsync("bulk-4xx");
        using var _ = user;
        Assert.Equal(BulkBatchStatus.Failed, (await factory.BulkBatchRowAsync(batch.Id, Ct)).Status);
        Assert.NotNull(factory.Storage.Find(batch.OriginalKey));

        await factory.CleanupBulkAsync(user.Id, now.AddHours(25), Ct);

        Assert.Equal(BulkBatchStatus.Expired, (await factory.BulkBatchRowAsync(batch.Id, Ct)).Status);
        Assert.Null(factory.Storage.Find(batch.OriginalKey));
    }

    [Fact]
    public async Task ACreatedBatchPastItsExpiryLosesItsPhoto()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var now = BulkAddClientExtensions.PinnedNow();
        user.Client.At(now);
        var batch = await factory.StartBulkAsync(user.Client, "bulk-3", Ct);

        await factory.CleanupBulkAsync(user.Id, now.AddHours(25), Ct);

        Assert.Equal(BulkBatchStatus.Expired, (await factory.BulkBatchRowAsync(batch.Id, Ct)).Status);
        Assert.Null(factory.Storage.Find(batch.OriginalKey));
    }

    // ---- Interrupted ----

    [Fact]
    public async Task AProcessingBatchOlderThan15MinutesFailsAsInterrupted()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var now = BulkAddClientExtensions.PinnedNow();
        user.Client.At(now);
        var batch = await factory.ProcessBulkAsync(user.Client, "bulk-3", Ct);

        var summary = await factory.CleanupBulkAsync(user.Id, now.AddMinutes(16), Ct);

        Assert.Equal(1, summary.Interrupted);
        var row = await factory.BulkBatchRowAsync(batch.Id, Ct);
        Assert.Equal(BulkBatchStatus.Failed, row.Status);
        Assert.Equal(BulkFailureCode.Interrupted, row.FailureCode);
    }

    [Fact]
    public async Task AnInterruptedBatchIsRetryableInTheStatusAnswer()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var now = BulkAddClientExtensions.PinnedNow();
        user.Client.At(now);
        var batch = await factory.ProcessBulkAsync(user.Client, "bulk-3", Ct);

        await factory.CleanupBulkAsync(user.Id, now.AddMinutes(16), Ct);

        var failure = (await user.Client.BulkStatusAsync(batch.Id, Ct)).GetProperty("failure");
        Assert.Equal("Interrupted", failure.GetProperty("code").GetString());
        Assert.True(failure.GetProperty("retryable").GetBoolean());
    }

    [Fact]
    public async Task AProcessingBatchYoungerThan15MinutesIsLeftAlone()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var now = BulkAddClientExtensions.PinnedNow();
        user.Client.At(now);
        var batch = await factory.ProcessBulkAsync(user.Client, "bulk-3", Ct);

        var summary = await factory.CleanupBulkAsync(user.Id, now.AddMinutes(14), Ct);

        Assert.Equal(0, summary.Interrupted);
        Assert.Equal(BulkBatchStatus.Processing, (await factory.BulkBatchRowAsync(batch.Id, Ct)).Status);
    }

    // ---- Flag off ----

    [Fact]
    public async Task WithTheFlagOffTheCleanupStillEndsExpiredBatches()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var now = BulkAddClientExtensions.PinnedNow();
        var batchId = await factory.InsertBulkBatchAsync(user.Id, BulkBatchStatus.Ready, now.AddHours(-1), now.AddHours(-25), counted: true, cancellationToken: Ct);
        await using var off = factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection([new KeyValuePair<string, string?>("BulkAdd:Enabled", "false")])));
        var storage = off.Services.GetRequiredService<FakeObjectStorage>();
        var staged = StorageKeys.BuildBulk(user.Id, batchId, Guid.NewGuid(), ImageVariant.Staged, "png");
        storage.Upload(staged, [1, 2, 3], "image/png");

        BulkCleanupSummary summary;
        await using (var scope = off.Services.CreateAsyncScope())
        {
            summary = await scope.ServiceProvider.GetRequiredService<BulkAddCleanupJob>().CleanupAsync(user.Id, now, Ct);
        }

        Assert.Equal(1, summary.Expired);
        Assert.Equal(BulkBatchStatus.Expired, (await factory.BulkBatchRowAsync(batchId, Ct)).Status);
        Assert.Null(storage.Find(staged));
    }

    // ---- Tombstones ----

    private async Task<(TestUser User, Guid BatchId, DateTimeOffset EndedAt)> CancelledAsync()
    {
        var (user, batch, now) = await ReadyAsync();
        using (var cancel = await user.Client.BulkCancelAsync(batch.Id, Ct))
        {
            Assert.Equal(HttpStatusCode.NoContent, cancel.StatusCode);
        }

        return (user, batch.Id, now);
    }

    [Fact]
    public async Task ATombstoneYoungerThanSevenDaysStays()
    {
        var (user, batchId, endedAt) = await CancelledAsync();
        using var _ = user;

        var summary = await factory.CleanupBulkAsync(user.Id, endedAt.AddDays(7).AddMinutes(-1), Ct);

        Assert.Equal(0, summary.TombstonesDeleted);
        Assert.True(await factory.WithDbAsync(db => db.BulkBatches.AnyAsync(batch => batch.Id == batchId, Ct)));
    }

    [Fact]
    public async Task ATombstoneOlderThanSevenDaysIsDeleted()
    {
        var (user, batchId, endedAt) = await CancelledAsync();
        using var _ = user;

        var summary = await factory.CleanupBulkAsync(user.Id, endedAt.AddDays(7).AddMinutes(1), Ct);

        Assert.Equal(1, summary.TombstonesDeleted);
        Assert.False(await factory.WithDbAsync(db => db.BulkBatches.AnyAsync(batch => batch.Id == batchId, Ct)));
    }

    [Fact]
    public async Task TheTombstoneAgeCountsFromTheEndNotTheCreation()
    {
        var (user, batch, now) = await ReadyAsync();
        using var _ = user;
        var confirmAt = now.AddHours(20);
        user.Client.At(confirmAt);
        var pieces = await factory.BulkPieceRowsAsync(batch.Id, Ct);
        await user.Client.BulkConfirmAsync(batch.Id, [pieces[0].Id], Ct);

        var summary = await factory.CleanupBulkAsync(user.Id, now.AddDays(7).AddHours(1), Ct);

        Assert.Equal(0, summary.TombstonesDeleted);
        Assert.True(await factory.WithDbAsync(db => db.BulkBatches.AnyAsync(row => row.Id == batch.Id, Ct)));
    }

    // ---- Orphans ----

    [Fact]
    public async Task AnObjectWithoutABatchRowIsDeleted()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var now = BulkAddClientExtensions.PinnedNow();
        var orphan = StorageKeys.BuildBulk(user.Id, Guid.NewGuid(), Guid.NewGuid(), ImageVariant.Staged, "png");
        factory.Storage.Upload(orphan, [1, 2, 3], "image/png");
        factory.Storage.Backdate(orphan, now.AddDays(-2));

        var summary = await factory.CleanupBulkAsync(user.Id, now, Ct);

        Assert.Equal(1, summary.OrphansDeleted);
        Assert.Null(factory.Storage.Find(orphan));
    }

    [Fact]
    public async Task TheObjectsOfALiveBatchAreNotOrphans()
    {
        var (user, batch, now) = await ReadyAsync();
        using var _ = user;
        var keys = KeysUnder(user.Id, StorageKeys.BulkBatchPrefix(user.Id, batch.Id));
        Assert.NotEmpty(keys);
        keys.ForEach(key => factory.Storage.Backdate(key, now.AddDays(-2)));

        var summary = await factory.CleanupBulkAsync(user.Id, now.AddMinutes(1), Ct);

        Assert.Equal(0, summary.OrphansDeleted);
        Assert.All(keys, key => Assert.NotNull(factory.Storage.Find(key)));
    }

    [Fact]
    public async Task ACleanupForOneUserLeavesAnotherUsersObjects()
    {
        using var user = await factory.CreateUserAsync(Ct);
        using var other = await factory.CreateUserAsync(Ct);
        var now = BulkAddClientExtensions.PinnedNow();
        var orphan = StorageKeys.BuildBulk(other.Id, Guid.NewGuid(), Guid.NewGuid(), ImageVariant.Staged, "png");
        factory.Storage.Upload(orphan, [1, 2, 3], "image/png");
        factory.Storage.Backdate(orphan, now.AddDays(-2));

        await factory.CleanupBulkAsync(user.Id, now, Ct);

        Assert.NotNull(factory.Storage.Find(orphan));
    }
}
