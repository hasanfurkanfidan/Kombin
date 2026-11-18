using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kombin.Api.Common.Storage;
using Kombin.Api.Features.Wardrobe.Jobs;
using Kombin.Api.Persistence.Entities.Wardrobe;
using Microsoft.EntityFrameworkCore;

namespace Kombin.Api.IntegrationTests;

/// <summary>"Toplu ekle" PR 2: <c>upload-url</c> and <c>process</c> (task BA3), idempotent as the design note says.</summary>
public sealed class BulkAddStartTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // A fixed instant at noon Istanbul, so nothing here depends on the hour the suite runs at.
    private static readonly DateTimeOffset Noon = new(2026, 11, 18, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task UploadUrlCreatesACreatedBatchWithAPrivateKeyAndAPresignedPut()
    {
        using var user = await factory.CreateUserAsync(Ct);
        user.Client.At(Noon);

        using var response = await user.Client.BulkUploadUrlRawAsync(cancellationToken: Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var batchId = body.GetProperty("batchId").GetGuid();
        Assert.Equal($"/api/v1/me/wardrobe/bulk/{batchId}", response.Headers.Location?.OriginalString);
        Assert.False(string.IsNullOrEmpty(body.GetProperty("uploadUrl").GetString()));
        Assert.Equal("image/jpeg", body.GetProperty("headers").GetProperty("Content-Type").GetString());
        Assert.True(body.GetProperty("expiresAt").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddMinutes(-1));

        var row = await factory.BulkBatchRowAsync(batchId, Ct);
        Assert.Equal((user.Id, BulkBatchStatus.Created), (row.UserId, row.Status));
        Assert.Equal($"bulk/{user.Id:D}/{batchId:D}/{batchId:D}/original.jpg", row.OriginalKey);
        Assert.False(StorageKeys.IsPublic(row.OriginalKey!));
        Assert.Equal(Noon.AddHours(24), row.ExpiresAt);
        Assert.Null(row.ProcessRequestedAt);
        Assert.False(row.Counted);
        Assert.Empty(await factory.BulkPieceRowsAsync(batchId, Ct));
    }

    [Theory]
    [InlineData("image/jpeg", "jpg")]
    [InlineData("image/png", "png")]
    [InlineData("image/webp", "webp")]
    [InlineData("image/heic", "heic")]
    [InlineData("IMAGE/JPEG; charset=binary", "jpg")]
    public async Task TheSingleAddsContentTypesAreAccepted(string contentType, string extension)
    {
        using var user = await factory.CreateUserAsync(Ct);

        using var response = await user.Client.BulkUploadUrlRawAsync(contentType, cancellationToken: Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var batchId = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("batchId").GetGuid();
        Assert.EndsWith($"/original.{extension}", (await factory.BulkBatchRowAsync(batchId, Ct)).OriginalKey, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("application/pdf", 1024L)]
    [InlineData("text/plain", 1024L)]
    [InlineData(null, 1024L)]
    [InlineData("image/jpeg", 0L)]
    [InlineData("image/jpeg", -5L)]
    [InlineData("image/jpeg", 8L * 1024 * 1024 + 1)]
    public async Task AnotherTypeOrSizeIsAValidationProblem(string? contentType, long sizeBytes)
    {
        using var user = await factory.CreateUserAsync(Ct);

        using var response = await user.Client.BulkUploadUrlRawAsync(contentType, sizeBytes, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Validation.Failed", (await response.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString());
        Assert.Equal(0, await factory.WithDbAsync(db => db.BulkBatches.CountAsync(batch => batch.UserId == user.Id, Ct)));
    }

    [Fact]
    public async Task EightMegabytesExactlyIsAccepted()
    {
        using var user = await factory.CreateUserAsync(Ct);

        using var response = await user.Client.BulkUploadUrlRawAsync(sizeBytes: 8L * 1024 * 1024, cancellationToken: Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task ProcessWithoutTheUploadIs409AndAnOversizedObjectIs400AndDeleted()
    {
        using var user = await factory.CreateUserAsync(Ct);
        using var created = await user.Client.BulkUploadUrlRawAsync(cancellationToken: Ct);
        var batchId = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("batchId").GetGuid();

        using var missing = await user.Client.BulkProcessAsync(batchId, Ct);
        Assert.Equal(HttpStatusCode.Conflict, missing.StatusCode);
        Assert.Equal("Wardrobe.UploadMissing", (await missing.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString());
        Assert.Equal(BulkBatchStatus.Created, (await factory.BulkBatchRowAsync(batchId, Ct)).Status);

        var key = (await factory.BulkBatchRowAsync(batchId, Ct)).OriginalKey!;
        factory.Storage.Upload(key, new byte[(8 * 1024 * 1024) + 1], "image/jpeg");
        using var tooLarge = await user.Client.BulkProcessAsync(batchId, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, tooLarge.StatusCode);
        Assert.Equal("Wardrobe.ImageTooLarge", (await tooLarge.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString());
        Assert.Null(factory.Storage.Find(key));
        Assert.Equal(BulkBatchStatus.Created, (await factory.BulkBatchRowAsync(batchId, Ct)).Status);
        Assert.Empty(factory.Jobs.JobsFor(batchId));
    }

    [Fact]
    public async Task ProcessEnqueuesTheAnalysisOnceAndAnotherCallSoonAfterEnqueuesNothing()
    {
        using var user = await factory.CreateUserAsync(Ct);
        user.Client.At(Noon);
        var started = await factory.StartBulkAsync(user.Client, cancellationToken: Ct);

        using var first = await user.Client.BulkProcessAsync(started.Id, Ct);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        var body = await first.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(started.Id, body.GetProperty("batchId").GetGuid());
        Assert.Equal("Processing", body.GetProperty("status").GetString());
        Assert.Single(factory.Jobs.JobsFor(started.Id));

        var row = await factory.BulkBatchRowAsync(started.Id, Ct);
        Assert.Equal((BulkBatchStatus.Processing, Noon, 0), (row.Status, row.ProcessRequestedAt, row.AnalysisAttempts));
        Assert.False(row.Counted);

        user.Client.At(Noon.AddMinutes(9));
        using var again = await user.Client.BulkProcessAsync(started.Id, Ct);
        Assert.Equal(HttpStatusCode.Accepted, again.StatusCode);
        Assert.Equal("Processing", (await again.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("status").GetString());
        Assert.Single(factory.Jobs.JobsFor(started.Id));
    }

    [Fact]
    public async Task AJobThatDiedLeavesProcessingAndProcessAfterTenMinutesStartsItAgain()
    {
        using var user = await factory.CreateUserAsync(Ct);
        user.Client.At(Noon);
        var started = await factory.ProcessBulkAsync(user.Client, cancellationToken: Ct);

        // The first run claimed the batch and died: Processing with one attempt, never finished.
        await factory.WithDbAsync(db => db.BulkBatches.Where(batch => batch.Id == started.Id).ExecuteUpdateAsync(set => set.SetProperty(batch => batch.AnalysisAttempts, 1), Ct));
        Assert.Equal(BulkAnalyzeOutcome.Skipped, await factory.AnalyzeBulkAsync(started.Id, Ct));

        user.Client.At(Noon.AddMinutes(10));
        using var again = await user.Client.BulkProcessAsync(started.Id, Ct);

        Assert.Equal(HttpStatusCode.Accepted, again.StatusCode);
        Assert.Equal(2, factory.Jobs.JobsFor(started.Id).Count);
        var row = await factory.BulkBatchRowAsync(started.Id, Ct);
        Assert.Equal((BulkBatchStatus.Processing, 0, Noon.AddMinutes(10)), (row.Status, row.AnalysisAttempts, row.ProcessRequestedAt));

        Assert.Equal(BulkAnalyzeOutcome.Ready, await factory.AnalyzeBulkAsync(started.Id, Ct));
    }

    [Fact]
    public async Task ReadyAndRefusedBatchesAreReturnedAsTheyAreWithNothingNewEnqueued()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var ready = await factory.RunBulkAsync(user.Client, "bulk-3", Ct);
        var refused = await factory.RunBulkAsync(user.Client, "bulk-refusal-closet", Ct);
        var jobsBefore = (factory.Jobs.JobsFor(ready.Id).Count, factory.Jobs.JobsFor(refused.Id).Count);

        foreach (var (batch, status) in new[] { (ready, "Ready"), (refused, "Refused") })
        {
            using var response = await user.Client.BulkProcessAsync(batch.Id, Ct);

            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            Assert.Equal(status, (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("status").GetString());
        }

        Assert.Equal(jobsBefore, (factory.Jobs.JobsFor(ready.Id).Count, factory.Jobs.JobsFor(refused.Id).Count));
        Assert.Equal(1, jobsBefore.Item1);
        Assert.Equal(3, (await factory.BulkPieceRowsAsync(ready.Id, Ct)).Count);
    }

    [Fact]
    public async Task AFailedBatchStartsAgainFromScratch()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var started = await factory.RunBulkAsync(user.Client, "bulk-busy-twice", Ct);
        var failed = await factory.BulkBatchRowAsync(started.Id, Ct);
        Assert.Equal((BulkBatchStatus.Failed, BulkFailureCode.VisionBusy), (failed.Status, failed.FailureCode));
        Assert.NotNull(factory.Storage.Find(started.OriginalKey));

        using var response = await user.Client.BulkProcessAsync(started.Id, Ct);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("Processing", (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("status").GetString());
        Assert.Equal(2, factory.Jobs.JobsFor(started.Id).Count);
        var row = await factory.BulkBatchRowAsync(started.Id, Ct);
        Assert.Equal((BulkBatchStatus.Processing, 0, false), (row.Status, row.AnalysisAttempts, row.Counted));
        Assert.Null(row.FailureCode);
        Assert.Null(row.FailureDetail);
    }

    [Theory]
    [InlineData("Confirmed", "Wardrobe.BulkAlreadyConfirmed")]
    [InlineData("Cancelled", "Wardrobe.BulkExpired")]
    [InlineData("Expired", "Wardrobe.BulkExpired")]
    public async Task AnEndedBatchCannotBeProcessed(string status, string errorCode)
    {
        using var user = await factory.CreateUserAsync(Ct);
        var batchId = await factory.InsertBulkBatchAsync(user.Id, Enum.Parse<BulkBatchStatus>(status), DateTimeOffset.UtcNow.AddHours(1), cancellationToken: Ct);

        using var response = await user.Client.BulkProcessAsync(batchId, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(errorCode, (await response.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task ABatchPastItsStagingTimeIsExpiredEvenBeforeTheCleanupRuns()
    {
        using var user = await factory.CreateUserAsync(Ct);
        user.Client.At(Noon);
        var started = await factory.StartBulkAsync(user.Client, cancellationToken: Ct);

        user.Client.At(Noon.AddHours(24));
        using var response = await user.Client.BulkProcessAsync(started.Id, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("Wardrobe.BulkExpired", (await response.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString());
        Assert.Empty(factory.Jobs.JobsFor(started.Id));
    }

    [Fact]
    public async Task AnotherUsersBatchOrAnUnknownOneIs404AndTheSame()
    {
        using var owner = await factory.CreateUserAsync(Ct);
        using var other = await factory.CreateUserAsync(Ct);
        var started = await factory.StartBulkAsync(owner.Client, cancellationToken: Ct);

        using var foreign = await other.Client.BulkProcessAsync(started.Id, Ct);
        using var unknown = await other.Client.BulkProcessAsync(Guid.NewGuid(), Ct);

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        var foreignProblem = await foreign.ReadProblemAsync(Ct);
        var unknownProblem = await unknown.ReadProblemAsync(Ct);
        Assert.Equal("Wardrobe.BulkNotFound", foreignProblem.GetProperty("errorCode").GetString());
        Assert.Equal(unknownProblem.GetProperty("errorCode").GetString(), foreignProblem.GetProperty("errorCode").GetString());
        Assert.Equal(unknownProblem.GetProperty("detail").GetString(), foreignProblem.GetProperty("detail").GetString());
        Assert.DoesNotContain(started.Id.ToString(), foreignProblem.GetRawText(), StringComparison.Ordinal);

        // The owner's batch is untouched: nothing was enqueued for it.
        Assert.Equal(BulkBatchStatus.Created, (await factory.BulkBatchRowAsync(started.Id, Ct)).Status);
        Assert.Empty(factory.Jobs.JobsFor(started.Id));
    }

    [Fact]
    public async Task EveryRouteNeedsASignIn()
    {
        using var anonymous = factory.CreateAnonymousClient();
        var id = Guid.NewGuid();

        using var upload = await anonymous.BulkUploadUrlRawAsync(cancellationToken: Ct);
        using var process = await anonymous.BulkProcessAsync(id, Ct);
        using var status = await anonymous.BulkStatusRawAsync(id, Ct);

        Assert.All(new[] { upload, process, status }, response => Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode));
    }
}
