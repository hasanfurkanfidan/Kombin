using Kombin.Api.Features.Wardrobe;
using Kombin.Api.Features.Wardrobe.Jobs;
using Kombin.Api.Persistence.Entities.Social;
using Kombin.Api.Persistence.Entities.Wardrobe;
using Microsoft.EntityFrameworkCore;

namespace Kombin.Api.IntegrationTests;

/// <summary>
/// Moderation of bulk pieces (<c>docs/design/bulk-add.md</c>, Moderation): asked after confirm, for the ticked pieces only, by one fire-and-forget
/// <c>bulk-add-moderate</c> per batch; until the verdict is <c>Allow</c> the item is not shareable, exactly like a single add's first seconds.
/// </summary>
public sealed class BulkAddModerationTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A new user's batch of the flavour, pieces 0 and 1 confirmed; the moderation job is only recorded.</summary>
    private async Task<(TestUser User, Guid BatchId, List<BulkPiece> Pieces)> ConfirmedAsync(string flavor = "bulk-3")
    {
        var user = await factory.CreateUserAsync(Ct);
        user.Client.At(BulkAddClientExtensions.PinnedNow());
        var batch = await factory.RunBulkAsync(user.Client, flavor, Ct);
        var pieces = await factory.BulkPieceRowsAsync(batch.Id, Ct);
        await user.Client.BulkConfirmAsync(batch.Id, [pieces[0].Id, pieces[1].Id], Ct);

        return (user, batch.Id, pieces);
    }

    private Task<List<WardrobeItem>> ItemsAsync(Guid userId) =>
        factory.WithDbAsync(db => db.WardrobeItems.AsNoTracking().Where(item => item.UserId == userId).ToListAsync(Ct));

    [Fact]
    public async Task AConfirmedItemHasNoVerdictBeforeTheJobRuns()
    {
        var (user, _, _) = await ConfirmedAsync();
        using var _1 = user;

        var items = await ItemsAsync(user.Id);

        Assert.Equal(2, items.Count);
        Assert.All(items, item => Assert.Null(item.ModerationVerdict));
    }

    [Fact]
    public async Task AConfirmedItemIsNotShareableBeforeTheJobRuns()
    {
        var (user, _, _) = await ConfirmedAsync();
        using var _1 = user;

        Assert.All(await ItemsAsync(user.Id), item => Assert.False(WardrobeModerator.IsShareable(item)));
    }

    [Fact]
    public async Task AnAllowVerdictMakesTheItemsShareable()
    {
        var (user, batchId, _) = await ConfirmedAsync();
        using var _1 = user;

        var moderated = await factory.ModerateBulkAsync(batchId, Ct);

        Assert.Equal(2, moderated);
        Assert.All(await ItemsAsync(user.Id), item =>
        {
            Assert.Equal(ModerationVerdict.Allow, item.ModerationVerdict);
            Assert.NotNull(item.ModeratedAt);
            Assert.True(WardrobeModerator.IsShareable(item));
        });
    }

    [Fact]
    public async Task AnNsfwPhotoLeavesTheItemsNotShareable()
    {
        var (user, batchId, _) = await ConfirmedAsync("bulk-3-nsfw");
        using var _1 = user;

        await factory.ModerateBulkAsync(batchId, Ct);

        Assert.All(await ItemsAsync(user.Id), item =>
        {
            Assert.Equal(ModerationVerdict.Block, item.ModerationVerdict);
            Assert.False(WardrobeModerator.IsShareable(item));
        });
    }

    [Fact]
    public async Task OnlyTheTickedPiecesAreModerated()
    {
        var (user, batchId, pieces) = await ConfirmedAsync();
        using var _1 = user;

        var moderated = await factory.ModerateBulkAsync(batchId, Ct);

        Assert.Equal(2, moderated);
        var items = await ItemsAsync(user.Id);
        Assert.Equivalent(new[] { pieces[0].Id, pieces[1].Id }, items.Select(item => item.Id).ToArray(), strict: true);
        Assert.All(items, item => Assert.NotNull(item.ModerationVerdict));
    }

    [Fact]
    public async Task ConfirmEnqueuesExactlyOneModerationJobForTheBatch()
    {
        var (user, batchId, _) = await ConfirmedAsync();
        using var _1 = user;

        var jobs = factory.Jobs.JobsFor(batchId).Where(job => job.Type == typeof(BulkAddModerateJob)).ToList();

        Assert.Single(jobs);
    }

    [Fact]
    public async Task ARepeatedConfirmEnqueuesNoSecondModerationJob()
    {
        var (user, batchId, pieces) = await ConfirmedAsync();
        using var _1 = user;

        await user.Client.BulkConfirmAsync(batchId, [pieces[0].Id], Ct);

        Assert.Single(factory.Jobs.JobsFor(batchId), job => job.Type == typeof(BulkAddModerateJob));
    }

    [Fact]
    public async Task NoModerationJobIsEnqueuedBeforeConfirm()
    {
        using var user = await factory.CreateUserAsync(Ct);
        user.Client.At(BulkAddClientExtensions.PinnedNow());

        var batch = await factory.RunBulkAsync(user.Client, "bulk-3", Ct);

        Assert.DoesNotContain(factory.Jobs.JobsFor(batch.Id), job => job.Type == typeof(BulkAddModerateJob));
    }
}
