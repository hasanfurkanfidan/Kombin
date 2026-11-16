using System.Net;
using Kombin.Api.Features.Catalog.Embeddings;
using Kombin.Api.Features.Catalog.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Kombin.Api.IntegrationTests;

/// <summary>
/// Task B7: one embedding run at a time. The nightly run, the admin panel's backfill and <c>POST /internal/catalog/embeddings/run</c> all end in
/// <see cref="ProductEmbeddingJob.EmbedAsync"/>, which takes a PostgreSQL advisory lock for the whole run. "A run is in progress" is made
/// deterministic with <see cref="FakeCatalogImageHandler.Hold"/>: the run is parked in the shop's answer until the test lets it go.
/// </summary>
[Collection(EmbeddingRuns.Name)]
public sealed class EmbeddingRunLockTests(ApiFactory factory)
{
    private const string RunUrl = "/api/v1/internal/catalog/embeddings/run";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string ImageUrl(string flavor = "plain") => $"https://img.runlock.test/{Guid.NewGuid():N}/{flavor}.jpg";

    private async Task<Guid> SeedAsync(string imageUrl)
    {
        using var client = factory.CreateAnonymousClient();

        return await client.SeedCatalogProductAsync("ust-giyim/tisort", "Navy", 2, imageUrl, Ct);
    }

    private Task<EmbeddingRunSummary> EmbedAsync(Guid productId, CancellationToken cancellationToken) =>
        factory.RunJobAsync<ProductEmbeddingJob, EmbeddingRunSummary>(job => job.EmbedAsync([productId], int.MaxValue, null, cancellationToken));

    /// <summary>Starts a run on its own thread and returns once it is parked in the gate (so it holds the lock, if there is one).</summary>
    private async Task<Task<EmbeddingRunSummary>> StartHeldRunAsync(Guid productId, FakeCatalogImageHandler.ImageGate gate, CancellationToken runToken)
    {
        var run = Task.Run(() => EmbedAsync(productId, runToken), Ct);
        await gate.Entered.Task.WaitAsync(Patience, Ct);

        return run;
    }

    private async Task<bool> ProductHasEmbeddingAsync(Guid productId) =>
        await factory.WithDbAsync(db => db.ProductEmbeddings.AsNoTracking().AnyAsync(row => row.ProductId == productId, Ct));

    [Fact]
    public async Task TwoConcurrentRunsOnlyTheFirstWorksAndTheSecondSkipsWithoutTouchingAnything()
    {
        var heldUrl = ImageUrl("gated");
        var held = await SeedAsync(heldUrl);
        var other = await SeedAsync(ImageUrl());
        using var gate = FakeCatalogImageHandler.Hold(heldUrl);
        var first = await StartHeldRunAsync(held, gate, Ct);

        try
        {
            var second = await EmbedAsync(other, Ct);

            Assert.True(second.AlreadyRunning);
            Assert.Equal(new EmbeddingRunSummary(0, 0, 0, 0, 0, 0, false, AlreadyRunning: true), second);
            Assert.False(await ProductHasEmbeddingAsync(other));
        }
        finally
        {
            gate.Open();
        }

        var finished = await first.WaitAsync(Patience, Ct);

        Assert.False(finished.AlreadyRunning);
        Assert.Equal(1, finished.Embedded);
        Assert.Equal(1, FakeCatalogImageHandler.Requests(heldUrl));
    }

    [Fact]
    public async Task AfterTheFirstRunEndsTheNextOneStartsNormally()
    {
        var heldUrl = ImageUrl("gated");
        var held = await SeedAsync(heldUrl);
        var other = await SeedAsync(ImageUrl());
        using var gate = FakeCatalogImageHandler.Hold(heldUrl);
        var first = await StartHeldRunAsync(held, gate, Ct);

        Assert.True((await EmbedAsync(other, Ct)).AlreadyRunning);
        gate.Open();
        await first.WaitAsync(Patience, Ct);

        var next = await EmbedAsync(other, Ct);

        Assert.False(next.AlreadyRunning);
        Assert.Equal(1, next.Embedded);
    }

    /// <summary>
    /// The nightly job and the admin backfill are different methods (so Hangfire's per-method <c>DisableConcurrentExecution</c> never saw each other);
    /// whichever runs first, the other one ends at once and successfully, and the held image is requested once.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheNightlyRunAndTheAdminBackfillExcludeEachOther(bool nightlyFirst)
    {
        var heldUrl = ImageUrl("gated");
        await SeedAsync(heldUrl);
        using var gate = FakeCatalogImageHandler.Hold(heldUrl);
        var running = Task.Run(
            () => factory.RunJobAsync<ProductEmbeddingJob, bool>(async job =>
            {
                await (nightlyFirst ? job.RunAsync(Ct) : job.BackfillAsync(ProductEmbeddingJob.MaxBackfillLimit, Ct));

                return true;
            }),
            Ct);
        await gate.Entered.Task.WaitAsync(Patience, Ct);

        try
        {
            var secondRan = Task.Run(
                () => factory.RunJobAsync<ProductEmbeddingJob, bool>(async job =>
                {
                    await (nightlyFirst ? job.BackfillAsync(ProductEmbeddingJob.MaxBackfillLimit, Ct) : job.RunAsync(Ct));

                    return true;
                }),
                Ct);

            // Without the guard the second run goes for the very same product and parks in the same gate.
            var completed = await Task.WhenAny(secondRan, Task.Delay(TimeSpan.FromSeconds(10), Ct)) == secondRan;

            Assert.True(completed, "the second run should end at once instead of waiting for the first");
            Assert.True(await secondRan);
        }
        finally
        {
            gate.Open();
        }

        Assert.True(await running.WaitAsync(Patience, Ct));
        Assert.Equal(1, FakeCatalogImageHandler.Requests(heldUrl));
    }

    [Fact]
    public async Task TheInternalEndpointAnswers409WhileARunHoldsTheLockAndWorksAfterwards()
    {
        var heldUrl = ImageUrl("gated");
        var held = await SeedAsync(heldUrl);
        var other = await SeedAsync(ImageUrl());
        using var client = factory.CreateClient();
        using var gate = FakeCatalogImageHandler.Hold(heldUrl);
        var first = await StartHeldRunAsync(held, gate, Ct);

        try
        {
            using var refused = await client.SendInternalAsync(HttpMethod.Post, RunUrl, new { productIds = new[] { other } }, Ct);

            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Equal("Catalog.EmbeddingRunInProgress", (await refused.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString());
            Assert.False(await ProductHasEmbeddingAsync(other));
        }
        finally
        {
            gate.Open();
        }

        await first.WaitAsync(Patience, Ct);

        using var accepted = await client.SendInternalAsync(HttpMethod.Post, RunUrl, new { productIds = new[] { other } }, Ct);

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.True(await ProductHasEmbeddingAsync(other));
    }

    [Fact]
    public async Task AFailedRunReleasesTheLockSoTheNextOneStarts()
    {
        var heldUrl = ImageUrl("gated");
        var held = await SeedAsync(heldUrl);
        var other = await SeedAsync(ImageUrl());
        var third = await SeedAsync(ImageUrl());
        using var gate = FakeCatalogImageHandler.Hold(heldUrl);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var failing = await StartHeldRunAsync(held, gate, cancellation.Token);

        // While it runs the lock is held (without the guard this run would simply work) ...
        Assert.True((await EmbedAsync(other, Ct)).AlreadyRunning);

        // ... the run dies in the middle of its work ...
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => failing);

        // ... and the lock went with it.
        var next = await EmbedAsync(third, Ct);

        Assert.False(next.AlreadyRunning);
        Assert.Equal(1, next.Embedded);
    }

    [Fact]
    public async Task TheLockIsFreedByTheServerWhenTheProcessHoldingItDies()
    {
        var runLock = factory.Services.GetRequiredService<EmbeddingRunLock>();
        var held = await runLock.TryAcquireAsync(Ct);
        Assert.NotNull(held);

        try
        {
            Assert.True(await runLock.IsHeldAsync(Ct));
            Assert.Null(await runLock.TryAcquireAsync(Ct));

            // The holder's session ends without releasing anything: what a crashed process looks like to the database.
            var terminated = await factory.WithDbAsync(db => db.Database
                .SqlQuery<bool>($"SELECT pg_terminate_backend(pid) AS \"Value\" FROM pg_locks WHERE locktype = 'advisory' AND classid = {EmbeddingRunLock.ClassId} AND objid = {EmbeddingRunLock.ObjectId} AND objsubid = 2 AND granted")
                .ToListAsync(Ct));
            Assert.Contains(true, terminated);

            await using var again = await runLock.TryAcquireAsync(Ct);

            Assert.NotNull(again);
        }
        finally
        {
            if (held is not null)
            {
                await held.DisposeAsync();
            }
        }

        Assert.False(await runLock.IsHeldAsync(Ct));
    }

    [Fact]
    public async Task TheAdminBackfillButtonQueuesNothingWhileARunIsInProgressAndSaysSoInTurkish()
    {
        var heldUrl = ImageUrl("gated");
        var held = await SeedAsync(heldUrl);
        using var browser = await factory.SignedInAdminAsync(Ct);
        using var gate = FakeCatalogImageHandler.Hold(heldUrl);
        var first = await StartHeldRunAsync(held, gate, Ct);
        const int Limit = 654;

        try
        {
            using var response = await browser.PostFormAsync("/admin/catalog", "/admin/catalog?handler=Backfill", [new("limit", Limit.ToString(System.Globalization.CultureInfo.InvariantCulture))], Ct);
            var page = WebUtility.HtmlDecode(await browser.GetHtmlAsync("/admin/catalog", Ct));

            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.DoesNotContain(factory.Jobs.Jobs, job => job.Type == typeof(ProductEmbeddingJob) && job.Args.Count > 0 && job.Args[0] is Limit);
            Assert.Contains("Görsel gömme çalışması şu an sürüyor", page, StringComparison.Ordinal);
            Assert.Empty(await factory.WithDbAsync(db => db.AdminAuditLog.Where(entry => entry.Action == "catalog.backfill" && entry.Summary!.StartsWith("limit=654;")).ToListAsync(Ct)));
        }
        finally
        {
            gate.Open();
        }

        await first.WaitAsync(Patience, Ct);
    }
}
