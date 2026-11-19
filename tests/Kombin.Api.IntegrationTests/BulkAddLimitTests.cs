using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kombin.Api.Persistence.Entities.Wardrobe;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Kombin.Api.IntegrationTests;

/// <summary>
/// The limits of "Toplu ekle" (task BA3): three open batches, eight counted photos an Istanbul day. Every time is pinned with <c>X-Test-Now</c> or
/// passed to the rows, so none of it depends on the hour the suite runs at (the 23:30 and 00:30 reruns are the proof).
/// </summary>
public sealed class BulkAddLimitTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // 15:00 in Istanbul on 18 November 2026; the Istanbul day runs from 2026-11-17T21:00Z to 2026-11-18T21:00Z.
    private static readonly DateTimeOffset Afternoon = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset DayEnds = new(2026, 11, 18, 21, 0, 0, TimeSpan.Zero);

    private async Task SeedCountedAsync(Guid userId, int count, DateTimeOffset at, BulkBatchStatus status = BulkBatchStatus.Confirmed, bool counted = true)
    {
        for (var index = 0; index < count; index++)
        {
            await factory.InsertBulkBatchAsync(userId, status, at.AddDays(2), processRequestedAt: at, counted: counted, cancellationToken: Ct);
        }
    }

    [Fact]
    public async Task TheFourthOpenBatchIs409AndEveryOneThatEndedMakesRoom()
    {
        using var user = await factory.CreateUserAsync(Ct);
        user.Client.At(Afternoon);
        var first = await factory.StartBulkAsync(user.Client, cancellationToken: Ct);
        await factory.StartBulkAsync(user.Client, cancellationToken: Ct);
        await factory.StartBulkAsync(user.Client, cancellationToken: Ct);

        using var fourth = await user.Client.BulkUploadUrlRawAsync(cancellationToken: Ct);

        Assert.Equal(HttpStatusCode.Conflict, fourth.StatusCode);
        Assert.Equal("Wardrobe.BulkTooManyOpen", (await fourth.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString());
        Assert.Equal(3, await factory.WithDbAsync(db => db.BulkBatches.CountAsync(batch => batch.UserId == user.Id, Ct)));

        // One that ended is no longer open.
        await factory.WithDbAsync(db => db.BulkBatches.Where(batch => batch.Id == first.Id).ExecuteUpdateAsync(set => set.SetProperty(batch => batch.Status, BulkBatchStatus.Cancelled), Ct));
        using var afterCancel = await user.Client.BulkUploadUrlRawAsync(cancellationToken: Ct);
        Assert.Equal(HttpStatusCode.Created, afterCancel.StatusCode);

        using var fifth = await user.Client.BulkUploadUrlRawAsync(cancellationToken: Ct);
        Assert.Equal(HttpStatusCode.Conflict, fifth.StatusCode);
    }

    [Theory]
    [InlineData("Confirmed")]
    [InlineData("Cancelled")]
    [InlineData("Expired")]
    public async Task EndedBatchesNeverCountAsOpen(string status)
    {
        using var user = await factory.CreateUserAsync(Ct);
        user.Client.At(Afternoon);
        for (var index = 0; index < 5; index++)
        {
            await factory.InsertBulkBatchAsync(user.Id, Enum.Parse<BulkBatchStatus>(status), Afternoon.AddHours(5), cancellationToken: Ct);
        }

        using var response = await user.Client.BulkUploadUrlRawAsync(cancellationToken: Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Theory]
    [InlineData("Processing")]
    [InlineData("Ready")]
    [InlineData("Refused")]
    [InlineData("Failed")]
    [InlineData("Created")]
    public async Task EveryOtherStatusIsOpen(string status)
    {
        using var user = await factory.CreateUserAsync(Ct);
        user.Client.At(Afternoon);
        for (var index = 0; index < 3; index++)
        {
            await factory.InsertBulkBatchAsync(user.Id, Enum.Parse<BulkBatchStatus>(status), Afternoon.AddHours(5), cancellationToken: Ct);
        }

        using var response = await user.Client.BulkUploadUrlRawAsync(cancellationToken: Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task ABatchPastItsStagingTimeIsNotOpenAndOtherUsersDoNotCount()
    {
        using var user = await factory.CreateUserAsync(Ct);
        using var other = await factory.CreateUserAsync(Ct);
        user.Client.At(Afternoon);
        for (var index = 0; index < 3; index++)
        {
            await factory.InsertBulkBatchAsync(user.Id, BulkBatchStatus.Ready, Afternoon.AddSeconds(-1), cancellationToken: Ct);
            await factory.InsertBulkBatchAsync(other.Id, BulkBatchStatus.Ready, Afternoon.AddHours(5), cancellationToken: Ct);
        }

        using var response = await user.Client.BulkUploadUrlRawAsync(cancellationToken: Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task SimultaneousStartsCannotTakeMoreThanTheOpenLimit()
    {
        using var user = await factory.CreateUserAsync(Ct);
        user.Client.At(Afternoon);

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => user.Client.BulkUploadUrlRawAsync(cancellationToken: Ct)));

        try
        {
            Assert.Equal(3, responses.Count(response => response.StatusCode == HttpStatusCode.Created));
            Assert.Equal(5, responses.Count(response => response.StatusCode == HttpStatusCode.Conflict));
            Assert.Equal(3, await factory.WithDbAsync(db => db.BulkBatches.CountAsync(batch => batch.UserId == user.Id, Ct)));
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Fact]
    public async Task TheNinthPhotoOfTheDayIs429WithTheTimeToMidnightAndNothingIsSpent()
    {
        using var user = await factory.CreateUserAsync(Ct);
        user.Client.At(Afternoon);
        await SeedCountedAsync(user.Id, 8, Afternoon.AddHours(-3));
        var started = await factory.StartBulkAsync(user.Client, cancellationToken: Ct);

        using var response = await user.Client.BulkProcessAsync(started.Id, Ct);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        var problem = await response.ReadProblemAsync(Ct);
        Assert.Equal("Wardrobe.BulkDailyLimit", problem.GetProperty("errorCode").GetString());
        var seconds = (int)(DayEnds - Afternoon).TotalSeconds;
        Assert.Equal(seconds, problem.GetProperty("retryAfterSeconds").GetInt32());
        Assert.Equal(seconds.ToString(CultureInfo.InvariantCulture), response.Headers.GetValues("Retry-After").Single());
        Assert.True(response.Headers.Contains("X-Correlation-ID"));

        var row = await factory.BulkBatchRowAsync(started.Id, Ct);
        Assert.Equal((BulkBatchStatus.Created, (DateTimeOffset?)null), (row.Status, row.ProcessRequestedAt));
        Assert.Empty(factory.Jobs.JobsFor(started.Id));
        Assert.NotNull(factory.Storage.Find(started.OriginalKey));
    }

    [Fact]
    public async Task TheEighthPhotoIsStillAllowed()
    {
        using var user = await factory.CreateUserAsync(Ct);
        user.Client.At(Afternoon);
        await SeedCountedAsync(user.Id, 7, Afternoon.AddHours(-3));
        var started = await factory.StartBulkAsync(user.Client, cancellationToken: Ct);

        using var response = await user.Client.BulkProcessAsync(started.Id, Ct);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task RefusalsCountButFailedAnalysesDoNot()
    {
        using var user = await factory.CreateUserAsync(Ct);
        user.Client.At(Afternoon);

        // A real refusal counts: the batch is Counted and so is its place in the day.
        var refused = await factory.RunBulkAsync(user.Client, "bulk-refusal-too_dark", Ct);
        Assert.True((await factory.BulkBatchRowAsync(refused.Id, Ct)).Counted);
        await SeedCountedAsync(user.Id, 6, Afternoon.AddHours(-3));
        var seventhAndEighth = await factory.StartBulkAsync(user.Client, cancellationToken: Ct);
        using var allowed = await user.Client.BulkProcessAsync(seventhAndEighth.Id, Ct);
        Assert.Equal(HttpStatusCode.Accepted, allowed.StatusCode);

        // Seven counted ones (the refusal and six) plus one in flight is the eighth: the next one is refused.
        var ninth = await factory.StartBulkAsync(user.Client, cancellationToken: Ct);
        using var refusedByLimit = await user.Client.BulkProcessAsync(ninth.Id, Ct);
        Assert.Equal(HttpStatusCode.TooManyRequests, refusedByLimit.StatusCode);

        // Once that analysis fails it is not counted, and the place is free again.
        await factory.WithDbAsync(db => db.BulkBatches.Where(batch => batch.Id == seventhAndEighth.Id).ExecuteUpdateAsync(
            set => set.SetProperty(batch => batch.Status, BulkBatchStatus.Failed).SetProperty(batch => batch.FailureCode, BulkFailureCode.VisionBusy), Ct));
        using var afterFailure = await user.Client.BulkProcessAsync(ninth.Id, Ct);
        Assert.Equal(HttpStatusCode.Accepted, afterFailure.StatusCode);
    }

    [Fact]
    public async Task FailedBatchesAreNotCountedAndARestartedOneNeverBlocksItself()
    {
        using var user = await factory.CreateUserAsync(Ct);
        user.Client.At(Afternoon);

        // Seven counted photos and two failed ones (open, but never counted): the eighth place is free.
        await SeedCountedAsync(user.Id, 7, Afternoon.AddHours(-2));
        await SeedCountedAsync(user.Id, 2, Afternoon.AddHours(-2), BulkBatchStatus.Failed, counted: false);
        var started = await factory.StartBulkAsync(user.Client, cancellationToken: Ct);

        using var response = await user.Client.BulkProcessAsync(started.Id, Ct);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        // That one fails; starting it again counts the seven of the others and never its own earlier request.
        await factory.WithDbAsync(db => db.BulkBatches.Where(batch => batch.Id == started.Id).ExecuteUpdateAsync(set => set.SetProperty(batch => batch.Status, BulkBatchStatus.Failed), Ct));
        using var restart = await user.Client.BulkProcessAsync(started.Id, Ct);
        Assert.Equal(HttpStatusCode.Accepted, restart.StatusCode);
    }

    [Fact]
    public async Task EachUsersDayIsTheirOwn()
    {
        using var busy = await factory.CreateUserAsync(Ct);
        using var user = await factory.CreateUserAsync(Ct);
        user.Client.At(Afternoon);
        await SeedCountedAsync(busy.Id, 8, Afternoon.AddHours(-3));
        var started = await factory.StartBulkAsync(user.Client, cancellationToken: Ct);

        using var response = await user.Client.BulkProcessAsync(started.Id, Ct);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Theory]
    [InlineData("2026-11-18T20:30:00Z", "2026-11-18T20:59:59Z", HttpStatusCode.TooManyRequests)] // 23:30 and 23:59:59 Istanbul: the same day
    [InlineData("2026-11-18T20:30:00Z", "2026-11-18T21:00:00Z", HttpStatusCode.Accepted)]        // midnight: a new day, a new allowance
    [InlineData("2026-11-18T20:30:00Z", "2026-11-18T21:30:00Z", HttpStatusCode.Accepted)]        // 00:30
    [InlineData("2026-11-18T21:30:00Z", "2026-11-18T21:45:00Z", HttpStatusCode.TooManyRequests)] // rows of 00:30 count at 00:45 the same night
    public async Task TheDayTurnsAtIstanbulMidnight(string countedAt, string processAt, HttpStatusCode expected)
    {
        using var user = await factory.CreateUserAsync(Ct);
        var at = DateTimeOffset.Parse(processAt, CultureInfo.InvariantCulture);
        user.Client.At(at);
        await SeedCountedAsync(user.Id, 8, DateTimeOffset.Parse(countedAt, CultureInfo.InvariantCulture));
        var started = await factory.StartBulkAsync(user.Client, cancellationToken: Ct);

        using var response = await user.Client.BulkProcessAsync(started.Id, Ct);

        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.TooManyRequests)
        {
            var next = at < DayEnds ? DayEnds : DayEnds.AddDays(1);
            Assert.Equal((int)(next - at).TotalSeconds, (await response.ReadProblemAsync(Ct)).GetProperty("retryAfterSeconds").GetInt32());
        }
    }

    [Fact]
    public async Task ABurstOfProcessCallsCannotSlipUnderTheLimit()
    {
        using var user = await factory.CreateUserAsync(Ct);
        user.Client.At(Afternoon);
        var batches = new List<StartedBatch>();
        for (var index = 0; index < 3; index++)
        {
            batches.Add(await factory.StartBulkAsync(user.Client, cancellationToken: Ct));
        }

        await SeedCountedAsync(user.Id, 6, Afternoon.AddHours(-3));

        // Eight in flight would need the first two; the third finds eight (six counted and two processing) and stops.
        var codes = new List<HttpStatusCode>();
        foreach (var batch in batches)
        {
            using var response = await user.Client.BulkProcessAsync(batch.Id, Ct);
            codes.Add(response.StatusCode);
        }

        Assert.Equal([HttpStatusCode.Accepted, HttpStatusCode.Accepted, HttpStatusCode.TooManyRequests], codes);
    }

    [Fact]
    public void UploadUrlAndProcessShareTheSingleAddsUploadsPolicy()
    {
        // The route is attached to the `uploads` policy like the single add (no new limiter): the endpoint metadata says so.
        var metadata = factory.Services.GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>().Endpoints
            .OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText is "/api/v1/me/wardrobe/bulk/upload-url" or "/api/v1/me/wardrobe/bulk/{id:guid}/process")
            .Select(endpoint => endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.RateLimiting.EnableRateLimitingAttribute>()?.PolicyName)
            .ToList();

        Assert.Equal(["uploads", "uploads"], metadata);
    }
}
