using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Kombin.Api.Common.Vision;
using Kombin.Api.Features.League;
using Kombin.Api.Features.League.Jobs;
using Kombin.Api.Features.League.Rules;
using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.League;
using Kombin.Api.Persistence.Entities.Recommendation;
using Kombin.Api.Persistence.Entities.Social;
using Microsoft.EntityFrameworkCore;
using static Kombin.Api.IntegrationTests.LeagueClientExtensions;

namespace Kombin.Api.IntegrationTests;

/// <summary>Kombin Ligi, PR 2: the day's worn outfit entering the league (<c>docs/design/league.md</c>, section 5), and the engine fill (section 6).</summary>
public sealed class LeagueEntryTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<JsonElement> TodayAsync(HttpClient client) => (await client.GetJsonAsync(Me, Ct)).GetProperty("today");

    private Task<LeagueEntriesSummary> RunAsync(DateTimeOffset now, params Guid[] userIds) => factory.RunLeagueEntriesAsync(now, userIds, Ct);

    [Fact]
    public async Task AWornOutfitEntersAndItsThumbnailsAreCopiedUnderKeysThatNameNoOne()
    {
        var day = NewDay();
        using var player = await factory.CreatePlayerAsync(day, Ct);
        var outfit = await factory.SeedOutfitAsync(player.Id, Ct);
        player.Client.At(Time(day, 9, 30));

        await player.Client.WearOutfitAsync(outfit.OutfitId, Ct);
        var summary = await RunAsync(Time(day, 9, 31), player.Id);

        Assert.Equal((1, 1), (summary.Resolved, summary.Competing));
        var entry = (await factory.EntryAsync(player.Id, day, Ct))!;
        Assert.Equal((LeagueEntryStatus.Competing, LeagueEntryKind.Human, outfit.OutfitId), (entry.Status, entry.Kind, entry.OutfitId));
        Assert.Equal(outfit.ItemIds.Order(), entry.PieceIds);
        Assert.Equal(3, entry.Pieces.Count);

        foreach (var (piece, index) in entry.Pieces.OrderBy(piece => piece.Position).Select((piece, index) => (piece, index)))
        {
            var key = Assert.IsType<string>(piece.ImageKey);
            Assert.StartsWith("league/", key, StringComparison.Ordinal);
            Assert.EndsWith("/thumb.webp", key, StringComparison.Ordinal);
            Assert.DoesNotContain(player.Id.ToString(), key, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(outfit.ItemIds[index].ToString(), key, StringComparison.OrdinalIgnoreCase);
            var copy = factory.Storage.Find(key)!;
            Assert.Equal("image/webp", copy.ContentType);
            Assert.NotEqual(Thumbnail, copy.Content);
            Assert.Equal(ImageData(Thumbnail), ImageData(copy.Content));
        }

        player.Client.At(Time(day, 10));
        var today = await TodayAsync(player.Client);
        Assert.Equal(day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), today.GetProperty("day").GetString());
        Assert.Equal(Time(day, 21), today.GetProperty("closesAt").GetDateTimeOffset());
        Assert.Equal("Competing", today.GetProperty("entry").GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, today.GetProperty("entry").GetProperty("reason").ValueKind);
        Assert.Equal(3, today.GetProperty("entry").GetProperty("pieces").GetArrayLength());
        Assert.Equal((0, 0, 10, 30), (today.GetProperty("servedPairs").GetInt32(), today.GetProperty("countedVotes").GetInt32(), today.GetProperty("target").GetInt32(), today.GetProperty("limit").GetInt32()));

        // A second run (or a GET) redoes nothing: no second copy of the images.
        var copies = entry.Pieces.Select(piece => piece.ImageKey).ToList();
        await RunAsync(Time(day, 9, 35), player.Id);
        Assert.Equal(copies, (await factory.EntryAsync(player.Id, day, Ct))!.Pieces.OrderBy(piece => piece.Position).Select(piece => piece.ImageKey));
    }

    [Fact]
    public async Task TheOwnerSeesTheEntryAtOnceWithoutWaitingForTheJob()
    {
        var day = NewDay();
        using var player = await factory.CreatePlayerAsync(day, Ct);
        var outfit = await factory.SeedOutfitAsync(player.Id, Ct);
        player.Client.At(Time(day, 8));
        await player.Client.WearOutfitAsync(outfit.OutfitId, Ct);

        var today = await TodayAsync(player.Client);

        Assert.Equal("Competing", today.GetProperty("entry").GetProperty("status").GetString());
        Assert.All(today.GetProperty("entry").GetProperty("pieces").EnumerateArray(), piece => Assert.Contains("/league/", piece.GetProperty("imageUrl").GetString(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task ASavedOutfitNeverEnters()
    {
        var day = NewDay();
        using var player = await factory.CreatePlayerAsync(day, Ct);
        var outfit = await factory.SeedOutfitAsync(player.Id, Ct);
        player.Client.At(Time(day, 11));

        using (var saved = await player.Client.PostAsync($"/api/v1/me/outfits/{outfit.OutfitId}/save", null, Ct))
        {
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        }

        await RunAsync(Time(day, 11, 1), player.Id);

        Assert.Null(await factory.EntryAsync(player.Id, day, Ct));
        Assert.Equal(JsonValueKind.Null, (await TodayAsync(player.Client)).GetProperty("entry").ValueKind);
    }

    [Fact]
    public async Task AWearAfterTheCloseDoesNotEnter()
    {
        var day = NewDay();
        using var player = await factory.CreatePlayerAsync(day, Ct);
        var outfit = await factory.SeedOutfitAsync(player.Id, Ct);
        player.Client.At(Time(day, 21, 5));

        await player.Client.WearOutfitAsync(outfit.OutfitId, Ct);
        await RunAsync(Time(day, 21, 6), player.Id);
        var today = await TodayAsync(player.Client);

        Assert.Null(await factory.EntryAsync(player.Id, day, Ct));
        Assert.Equal(JsonValueKind.Null, today.GetProperty("entry").ValueKind);

        // Nor the next day's: the wear belongs to no league day.
        await RunAsync(Time(day.AddDays(1), 0, 5), player.Id);
        Assert.Null(await factory.EntryAsync(player.Id, day.AddDays(1), Ct));
    }

    [Fact]
    public async Task TheEntryFollowsTheLatestWearUntilItIsFirstServedThenItIsFixed()
    {
        var day = NewDay();
        using var player = await factory.CreatePlayerAsync(day, Ct);
        using var voter = await factory.CreatePlayerAsync(day, Ct);
        await factory.InsertEngineEntriesAsync(day, 2, Ct);
        var morning = await factory.SeedOutfitAsync(player.Id, Ct);
        var noon = await factory.SeedOutfitAsync(player.Id, Ct);
        var evening = await factory.SeedOutfitAsync(player.Id, Ct);

        player.Client.At(Time(day, 8));
        await player.Client.WearOutfitAsync(morning.OutfitId, Ct);
        await RunAsync(Time(day, 8, 1), player.Id);
        var firstCopies = (await factory.EntryAsync(player.Id, day, Ct))!.Pieces.Select(piece => piece.ImageKey!).ToList();

        player.Client.At(Time(day, 11));
        await player.Client.WearOutfitAsync(noon.OutfitId, Ct);
        await RunAsync(Time(day, 11, 1), player.Id);

        var followed = (await factory.EntryAsync(player.Id, day, Ct))!;
        Assert.Equal((noon.OutfitId, LeagueEntryStatus.Competing), (followed.OutfitId, followed.Status));
        Assert.Equal(noon.ItemIds.Order(), followed.PieceIds);
        Assert.All(firstCopies, key => Assert.Null(factory.Storage.Find(key)));

        // A voter is shown it: from now on it is fixed for the day.
        voter.Client.At(Time(day, 12));
        var next = await voter.Client.NextPairAsync(Ct);
        Assert.Contains(followed.Pieces.Select(piece => factory.Storage.GetPublicUrl(piece.ImageKey!)).First(), next.Pair().Images("left").Concat(next.Pair().Images("right")));
        Assert.NotNull((await factory.EntryAsync(player.Id, day, Ct))!.LockedAt);

        player.Client.At(Time(day, 18));
        await player.Client.WearOutfitAsync(evening.OutfitId, Ct);
        await RunAsync(Time(day, 18, 1), player.Id);

        var fixedEntry = (await factory.EntryAsync(player.Id, day, Ct))!;
        Assert.Equal(noon.OutfitId, fixedEntry.OutfitId);
        Assert.Equal(followed.Pieces.Select(piece => piece.ImageKey), fixedEntry.Pieces.Select(piece => piece.ImageKey));
    }

    [Theory]
    [InlineData("Block")]
    [InlineData("Review")]
    public async Task AnOutfitWithAPieceTheSafetyFilterDidNotAllowDoesNotCompete(string verdictName)
    {
        var verdict = Enum.Parse<ModerationVerdict>(verdictName);
        var day = NewDay();
        using var player = await factory.CreatePlayerAsync(day, Ct);
        var outfit = await factory.SeedOutfitAsync(player.Id, [new("ust-giyim/gomlek"), new("alt-giyim/pantolon", verdict), new("ayakkabi/sneaker")], cancellationToken: Ct);
        player.Client.At(Time(day, 10));

        await player.Client.WearOutfitAsync(outfit.OutfitId, Ct);
        await RunAsync(Time(day, 10, 1), player.Id);

        var entry = (await factory.EntryAsync(player.Id, day, Ct))!;
        Assert.Equal((LeagueEntryStatus.NotCompeting, LeagueEntryReason.UnsafePiece), (entry.Status, entry.Reason));
        Assert.All(entry.Pieces, piece => Assert.Null(piece.ImageKey));
        var today = await TodayAsync(player.Client);
        Assert.Equal("UnsafePiece", today.GetProperty("entry").GetProperty("reason").GetString());
        Assert.Equal(LeagueCopy.UnsafePiece, today.GetProperty("entry").GetProperty("message").GetString());
    }

    [Fact]
    public async Task APieceNotYetModeratedWaitsUntilTheJobAsksTheSafetyFilter()
    {
        var day = NewDay();
        using var player = await factory.CreatePlayerAsync(day, Ct);
        var outfit = await factory.SeedOutfitAsync(player.Id, [new("ust-giyim/gomlek"), new("alt-giyim/pantolon", null), new("ayakkabi/sneaker", null, "moderation-down")], cancellationToken: Ct);
        player.Client.At(Time(day, 10));
        await player.Client.WearOutfitAsync(outfit.OutfitId, Ct);

        // GET /league/me never waits for the filter.
        Assert.Equal("Pending", (await TodayAsync(player.Client)).GetProperty("entry").GetProperty("status").GetString());

        // The job asks; the filter answers one piece and is down for the other: still pending, nothing copied.
        var first = await RunAsync(Time(day, 10, 2), player.Id);
        Assert.Equal((2, 1), (first.ModerationCalls, first.Pending));
        Assert.Equal(ModerationVerdict.Allow, (await factory.ItemRowAsync(outfit.ItemIds[1], Ct)).ModerationVerdict);
        var waiting = (await factory.EntryAsync(player.Id, day, Ct))!;
        Assert.Equal(LeagueEntryStatus.Pending, waiting.Status);
        Assert.All(waiting.Pieces, piece => Assert.Null(piece.ImageKey));

        // The filter is back: the next run (the entry is pending, so it is looked at without a new wear) makes it compete.
        factory.Storage.Upload((await factory.ItemRowAsync(outfit.ItemIds[2], Ct)).ImageCutoutKey!, FakeVisionClient.CutoutFor("ok"), "image/png");
        var second = await RunAsync(Time(day, 10, 30), player.Id);

        Assert.Equal((1, 1), (second.ModerationCalls, second.Competing));
        Assert.Equal(LeagueEntryStatus.Competing, (await factory.EntryAsync(player.Id, day, Ct))!.Status);
    }

    [Fact]
    public async Task AnOutfitWithAPieceThePlayerDoesNotOwnOrTooFewPiecesIsIncomplete()
    {
        var day = NewDay();
        using var player = await factory.CreatePlayerAsync(day, Ct);
        using var other = await factory.CreatePlayerAsync(day, Ct);
        using var anonymous = factory.CreateAnonymousClient();
        var product = await anonymous.SeedProductAsync("dis-giyim/ceket", "Navy", 3, cancellationToken: Ct);
        var withGap = await factory.SeedOutfitAsync(player.Id, [.. Outfit3.Select(slug => new LeaguePiece(slug))], gapProductId: product, cancellationToken: Ct);
        var single = await factory.SeedOutfitAsync(other.Id, [new("ust-giyim/gomlek"), new("aksesuar/canta")], cancellationToken: Ct);
        player.Client.At(Time(day, 10));
        other.Client.At(Time(day, 10));

        await player.Client.WearOutfitAsync(withGap.OutfitId, Ct);
        await other.Client.WearOutfitAsync(single.OutfitId, Ct);
        await RunAsync(Time(day, 10, 1), player.Id, other.Id);

        Assert.Equal(LeagueEntryReason.IncompleteOutfit, (await factory.EntryAsync(player.Id, day, Ct))!.Reason);
        Assert.Equal(LeagueEntryReason.IncompleteOutfit, (await factory.EntryAsync(other.Id, day, Ct))!.Reason);
        Assert.Equal(LeagueCopy.IncompleteOutfit, (await TodayAsync(player.Client)).GetProperty("entry").GetProperty("message").GetString());
    }

    [Fact]
    public async Task AnArchivedPieceMakesTheOutfitUnavailable()
    {
        var day = NewDay();
        using var player = await factory.CreatePlayerAsync(day, Ct);
        var outfit = await factory.SeedOutfitAsync(player.Id, Ct);
        player.Client.At(Time(day, 10));
        await player.Client.WearOutfitAsync(outfit.OutfitId, Ct);
        await factory.WithDbAsync(db => db.WardrobeItems.Where(item => item.Id == outfit.ItemIds[0]).ExecuteUpdateAsync(set => set.SetProperty(item => item.IsArchived, true), Ct));

        await RunAsync(Time(day, 10, 1), player.Id);

        Assert.Equal(LeagueEntryReason.PieceUnavailable, (await factory.EntryAsync(player.Id, day, Ct))!.Reason);
        Assert.Equal(LeagueCopy.PieceUnavailable, (await TodayAsync(player.Client)).GetProperty("entry").GetProperty("message").GetString());
    }

    [Fact]
    public async Task AnOutfitThatCompetedRecentlySitsOutWithTheRulesLine()
    {
        // Three days back is another test's NewDay; a block keeps both days this test's own (a close there would read this row).
        var day = NewBlock(4).AddDays(3);
        using var player = await factory.CreatePlayerAsync(day, Ct);
        var outfit = await factory.SeedOutfitAsync(player.Id, Ct);

        // Three days ago the same pieces (bar an accessory) competed at the close.
        await factory.WithDbAsync(async db =>
        {
            var earlier = new LeagueEntry
            {
                Day = day.AddDays(-3),
                UserId = player.Id,
                Kind = LeagueEntryKind.Human,
                Audience = Audience.Unisex,
                PieceIds = [.. outfit.ItemIds.Order()],
                PieceSetHash = new string('c', LeagueEntry.PieceSetHashLength),
                Status = LeagueEntryStatus.Competing,
            };
            db.LeagueEntries.Add(earlier);
            db.LeagueDailyScores.Add(new LeagueDailyScore { UserId = player.Id, Day = earlier.Day, EntryId = earlier.Id, Competed = true });
            await db.SaveChangesAsync(Ct);
            return 0;
        });
        player.Client.At(Time(day, 10));

        await player.Client.WearOutfitAsync(outfit.OutfitId, Ct);
        await RunAsync(Time(day, 10, 1), player.Id);

        var entry = (await factory.EntryAsync(player.Id, day, Ct))!.Pieces;
        Assert.All(entry, piece => Assert.Null(piece.ImageKey));
        var today = (await TodayAsync(player.Client)).GetProperty("entry");
        Assert.Equal(("NotCompeting", "RecentlyCompeted"), (today.GetProperty("status").GetString(), today.GetProperty("reason").GetString()));
        Assert.Equal(LeagueCopy.RecentlyCompeted, today.GetProperty("message").GetString());
        Assert.Equal("Bu kombin yakın zamanda yarıştı, bugün yarışmıyor.", today.GetProperty("message").GetString());
        Assert.All(today.GetProperty("pieces").EnumerateArray(), piece => Assert.NotNull(piece.GetProperty("imageUrl").GetString()));
    }

    [Fact]
    public async Task ABirthYearSetAfterJoiningThatKeepsThePlayerOutStopsTheEntry()
    {
        var day = NewDay();
        using var player = await factory.CreatePlayerAsync(day, Ct);
        var outfit = await factory.SeedOutfitAsync(player.Id, Ct);
        using (var profile = await player.Client.PutAsJsonAsync("/api/v1/me/profile", new { birthYear = day.Year - 18 }, Ct))
        {
            Assert.Equal(HttpStatusCode.OK, profile.StatusCode);
        }

        player.Client.At(Time(day, 10));
        await player.Client.WearOutfitAsync(outfit.OutfitId, Ct);
        await RunAsync(Time(day, 10, 1), player.Id);

        Assert.Equal(LeagueEntryReason.AgeRestricted, (await factory.EntryAsync(player.Id, day, Ct))!.Reason);
        Assert.Equal(LeagueCopy.AgeRestricted, (await TodayAsync(player.Client)).GetProperty("entry").GetProperty("message").GetString());
    }

    [Fact]
    public async Task OnlyAJoinedPlayerEntersAndLeavingReleasesTheCopies()
    {
        var day = NewDay();
        using var stranger = await factory.CreateUserAsync(Ct);
        using var player = await factory.CreatePlayerAsync(day, Ct);
        var strangerOutfit = await factory.SeedOutfitAsync(stranger.Id, Ct);
        var outfit = await factory.SeedOutfitAsync(player.Id, Ct);
        stranger.Client.At(Time(day, 9));
        player.Client.At(Time(day, 9));
        await stranger.Client.WearOutfitAsync(strangerOutfit.OutfitId, Ct);
        await player.Client.WearOutfitAsync(outfit.OutfitId, Ct);
        await RunAsync(Time(day, 9, 1), stranger.Id, player.Id);

        Assert.Null(await factory.EntryAsync(stranger.Id, day, Ct));
        var copies = (await factory.EntryAsync(player.Id, day, Ct))!.Pieces.Select(piece => piece.ImageKey!).ToList();
        Assert.All(copies, key => Assert.NotNull(factory.Storage.Find(key)));

        using (var left = await player.Client.LeaveLeagueAsync(Ct))
        {
            Assert.Equal(HttpStatusCode.NoContent, left.StatusCode);
        }

        var summary = await RunAsync(Time(day, 9, 5), player.Id);

        Assert.Equal(3, summary.CopiesDeleted);
        Assert.All(copies, key => Assert.Null(factory.Storage.Find(key)));
        Assert.All((await factory.EntryAsync(player.Id, day, Ct))!.Pieces, piece => Assert.Null(piece.ImageKey));
    }

    [Fact]
    public async Task WithdrawingIsFinalForTheDayAndTheCopiesGoWithTheNextRun()
    {
        var day = NewDay();
        using var player = await factory.CreatePlayerAsync(day, Ct);
        var first = await factory.SeedOutfitAsync(player.Id, Ct);
        var second = await factory.SeedOutfitAsync(player.Id, Ct);

        player.Client.At(Time(day, 9));
        using (var none = await player.Client.DeleteAsync("/api/v1/league/me/entry", Ct))
        {
            Assert.Equal((HttpStatusCode.NotFound, "League.NoEntryToday"), (none.StatusCode, (await none.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString()));
        }

        await player.Client.WearOutfitAsync(first.OutfitId, Ct);
        await RunAsync(Time(day, 9, 1), player.Id);
        var copies = (await factory.EntryAsync(player.Id, day, Ct))!.Pieces.Select(piece => piece.ImageKey!).ToList();

        using (var withdrawn = await player.Client.DeleteAsync("/api/v1/league/me/entry", Ct))
        {
            Assert.Equal(HttpStatusCode.NoContent, withdrawn.StatusCode);
        }

        using (var again = await player.Client.DeleteAsync("/api/v1/league/me/entry", Ct))
        {
            Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
        }

        // No re-roll: another worn outfit does not enter today.
        player.Client.At(Time(day, 10));
        await player.Client.WearOutfitAsync(second.OutfitId, Ct);
        await RunAsync(Time(day, 10, 1), player.Id);

        var entry = (await factory.EntryAsync(player.Id, day, Ct))!;
        Assert.Equal((LeagueEntryStatus.Withdrawn, first.OutfitId), (entry.Status, entry.OutfitId));
        Assert.All(copies, key => Assert.Null(factory.Storage.Find(key)));
        Assert.Equal("Withdrawn", (await TodayAsync(player.Client)).GetProperty("entry").GetProperty("status").GetString());

        player.Client.At(Time(day, 21));
        using var closed = await player.Client.DeleteAsync("/api/v1/league/me/entry", Ct);
        Assert.Equal((HttpStatusCode.Conflict, "League.DayClosed"), (closed.StatusCode, (await closed.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString()));
    }

    [Fact]
    public async Task WithdrawingNeedsAPlayer()
    {
        using var stranger = await factory.CreateUserAsync(Ct);

        using var response = await stranger.Client.DeleteAsync("/api/v1/league/me/entry", Ct);

        Assert.Equal((HttpStatusCode.Forbidden, "League.NotJoined"), (response.StatusCode, (await response.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString()));
    }

    [Fact]
    public async Task EngineOutfitsTopUpASmallPoolFromTheCatalogOnly()
    {
        var day = NewBlock(2).AddDays(1); // yesterday is used too: both days are this test's own
        using var anonymous = factory.CreateAnonymousClient();
        foreach (var (category, colour, formality) in new[]
        {
            ("ust-giyim/gomlek", "White", 3), ("ust-giyim/gomlek", "Blue", 3), ("ust-giyim/gomlek", "Grey", 4),
            ("alt-giyim/pantolon", "Navy", 3), ("alt-giyim/pantolon", "Black", 4), ("alt-giyim/pantolon", "Beige", 3),
            ("ayakkabi/loafer", "Black", 4), ("ayakkabi/loafer", "Brown", 3), ("ayakkabi/sneaker", "White", 3),

            // Winter days ask for a layer.
            ("dis-giyim/kaban", "Beige", 4), ("dis-giyim/ceket", "Navy", 3), ("dis-giyim/kaban", "Grey", 3),
        })
        {
            await anonymous.SeedProductAsync(category, colour, formality, audience: "Women", cancellationToken: Ct);
        }

        using var player = await factory.CreatePlayerAsync(day, Ct);
        await factory.InsertEntryAsync(day, player.Id, Audience.Women, Occasion.Office, cancellationToken: Ct);
        await factory.InsertEntryAsync(day.AddDays(-1), player.Id, Audience.Unisex, null, cancellationToken: Ct);

        // A bucket with a human entry yesterday only is not filled (every pair needs a human outfit of today).
        using var yesterdayOnly = await factory.CreatePlayerAsync(day, Ct);
        await factory.InsertEntryAsync(day.AddDays(-1), yesterdayOnly.Id, Audience.Men, Occasion.Weekend, cancellationToken: Ct);

        var first = await factory.RunJobAsync<LeagueFillJob, LeagueFillSummary>(job => job.FillAsync(day, Time(day, 6), Ct));
        var second = await factory.RunJobAsync<LeagueFillJob, LeagueFillSummary>(job => job.FillAsync(day, Time(day, 6, 15), Ct));
        var engines = await factory.WithDbAsync(db => db.LeagueEntries.AsNoTracking().Include(entry => entry.Pieces).Where(entry => entry.Day == day && entry.Kind == LeagueEntryKind.Engine).ToListAsync(Ct));

        // One bucket (Women, Office); the Unisex one and yesterday's (Men, Weekend) are not filled. At most 20 a run, never past 30 with the human entry.
        Assert.Equal(1, first.Buckets);
        Assert.InRange(first.Added, 1, 20);
        Assert.InRange(engines.Count, first.Added, 29);
        Assert.Equal(engines.Count, first.Added + second.Added);
        Assert.Equal(engines.Count, engines.Select(entry => entry.PieceSetHash).Distinct().Count());
        Assert.All(engines, entry =>
        {
            Assert.Equal((null, LeagueEntryStatus.Competing, Audience.Women, Occasion.Office), (entry.UserId, entry.Status, entry.Audience, entry.Occasion));
            Assert.True(entry.Pieces.Count >= 3);
            Assert.All(entry.Pieces, piece => Assert.True(piece is { ImageKey: null, WardrobeItemId: null, ProductId: not null, ImageUrl: not null }));
        });

        // After the close nothing is added.
        Assert.Equal(0, (await factory.RunJobAsync<LeagueFillJob, LeagueFillSummary>(job => job.FillAsync(day, Time(day, 21), Ct))).Added);
    }

    // Night review (task L3-fix), finding 4: a player who leaves while the job waits for the safety filter never gets a competing entry.
    [Fact]
    public async Task LeavingWhileTheSafetyFilterIsAskedLeavesNoCompetingEntry()
    {
        var day = NewDay();
        using var player = await factory.CreatePlayerAsync(day, Ct);
        var marker = Guid.NewGuid().ToString("N");
        var hold = FakeVisionClient.HoldModeration(marker);
        var outfit = await factory.SeedOutfitAsync(
            player.Id, [new LeaguePiece(Outfit3[0], Verdict: null, Cutout: $"hold:{marker}"), new LeaguePiece(Outfit3[1]), new LeaguePiece(Outfit3[2])], cancellationToken: Ct);
        player.Client.At(Time(day, 9));
        await player.Client.WearOutfitAsync(outfit.OutfitId, Ct);

        var run = factory.RunLeagueEntriesAsync(Time(day, 9, 1), [player.Id], Ct);
        await hold.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15), Ct);
        using (var left = await player.Client.LeaveLeagueAsync(Ct))
        {
            Assert.Equal(HttpStatusCode.NoContent, left.StatusCode);
        }

        hold.Release();
        await run;

        var entry = await factory.EntryAsync(player.Id, day, Ct);
        Assert.True(entry is null or { Status: not LeagueEntryStatus.Competing }, $"the player left, yet the entry is {entry?.Status}");
        Assert.All(entry?.Pieces ?? [], piece => Assert.Null(piece.ImageKey));
    }

    // Finding 5: until an entry is first shown, a birth year, an archived piece and a dismissed outfit reach it, and its copies go.
    [Fact]
    public async Task UntilItIsFirstShownTheEntryFollowsABirthYearAnArchivedPieceAndADismissedOutfit()
    {
        var day = NewDay();
        using var aged = await factory.CreatePlayerAsync(day, Ct);
        using var archiving = await factory.CreatePlayerAsync(day, Ct);
        using var dismissing = await factory.CreatePlayerAsync(day, Ct);
        var outfits = new Dictionary<Guid, SeededOutfit>();
        foreach (var player in new[] { aged, archiving, dismissing })
        {
            outfits[player.Id] = await factory.SeedOutfitAsync(player.Id, Ct);
            player.Client.At(Time(day, 9));
            await player.Client.WearOutfitAsync(outfits[player.Id].OutfitId, Ct);
        }

        await RunAsync(Time(day, 9, 1), aged.Id, archiving.Id, dismissing.Id);
        var copies = new List<string>();
        foreach (var player in new[] { aged, archiving, dismissing })
        {
            var entry = (await factory.EntryAsync(player.Id, day, Ct))!;
            Assert.Equal(LeagueEntryStatus.Competing, entry.Status);
            copies.AddRange(entry.Pieces.Select(piece => piece.ImageKey!));
        }

        // Nobody has been shown these outfits yet. A birth year that keeps the player out, a piece archived, the outfit dismissed:
        aged.Client.At(Time(day, 10));
        using (var profile = await aged.Client.PutAsJsonAsync("/api/v1/me/profile", new { birthYear = day.Year - 18 }, Ct))
        {
            Assert.Equal(HttpStatusCode.OK, profile.StatusCode);
        }

        await factory.WithDbAsync(db => db.WardrobeItems.Where(item => item.Id == outfits[archiving.Id].ItemIds[1]).ExecuteUpdateAsync(set => set.SetProperty(item => item.IsArchived, true), Ct));
        dismissing.Client.At(Time(day, 10));
        using (var dismissed = await dismissing.Client.DeleteAsync($"/api/v1/me/outfits/{outfits[dismissing.Id].OutfitId}?reason=Other", Ct))
        {
            Assert.Equal(HttpStatusCode.NoContent, dismissed.StatusCode);
        }

        // The job reaches competing entries nobody has seen (the wears are older than its look-back); the owner's GET resolves at once.
        await RunAsync(Time(day, 10, 1), aged.Id, archiving.Id);
        dismissing.Client.At(Time(day, 10, 2));
        var today = (await TodayAsync(dismissing.Client)).GetProperty("entry");

        Assert.Equal(LeagueEntryReason.AgeRestricted, (await factory.EntryAsync(aged.Id, day, Ct))!.Reason);
        Assert.Equal(LeagueEntryReason.PieceUnavailable, (await factory.EntryAsync(archiving.Id, day, Ct))!.Reason);
        Assert.Equal(("NotCompeting", "NotWorn"), (today.GetProperty("status").GetString(), today.GetProperty("reason").GetString()));
        Assert.Equal(LeagueCopy.NotWorn, today.GetProperty("message").GetString());
        foreach (var player in new[] { aged, archiving, dismissing })
        {
            var entry = (await factory.EntryAsync(player.Id, day, Ct))!;
            Assert.Equal(LeagueEntryStatus.NotCompeting, entry.Status);
            Assert.All(entry.Pieces, piece => Assert.Null(piece.ImageKey));
        }

        Assert.All(copies, key => Assert.Null(factory.Storage.Find(key)));
    }

    // Finding 8: a piece added from the catalog is its product in the repeat rule, so archiving it and adding it again changes nothing.
    [Fact]
    public async Task ACatalogPieceArchivedAndAddedAgainIsStillThePieceThatCompeted()
    {
        var day = NewDay();
        using var anonymous = factory.CreateAnonymousClient();
        var sneakers = await anonymous.SeedProductAsync("ayakkabi/sneaker", "White", 3, cancellationToken: Ct);
        using var player = await factory.CreatePlayerAsync(day, Ct);

        // Today: two photo pieces and the sneakers, added from the catalog again (a new wardrobe item of the same product).
        var outfit = await factory.SeedOutfitAsync(
            player.Id, [new LeaguePiece(Outfit3[0]), new LeaguePiece(Outfit3[1]), new LeaguePiece(Outfit3[2], ProductId: sneakers)], cancellationToken: Ct);

        // Ten days ago (outside the 7-day near rule) the same outfit competed with the earlier, since archived, wardrobe item of those sneakers.
        Guid[] expected = [.. new[] { outfit.ItemIds[0], outfit.ItemIds[1], sneakers }.Order()];
        await factory.WithDbAsync(async db =>
        {
            var earlier = new LeagueEntry
            {
                Day = day.AddDays(-10),
                UserId = player.Id,
                Kind = LeagueEntryKind.Human,
                Audience = Audience.Unisex,
                PieceIds = expected,
                PieceSetHash = RepeatRule.Hash(expected),
                Status = LeagueEntryStatus.Competing,
            };
            db.LeagueEntries.Add(earlier);
            db.LeagueDailyScores.Add(new LeagueDailyScore { UserId = player.Id, Day = earlier.Day, EntryId = earlier.Id, Competed = true });
            await db.SaveChangesAsync(Ct);
            return 0;
        });

        player.Client.At(Time(day, 10));
        await player.Client.WearOutfitAsync(outfit.OutfitId, Ct);
        await RunAsync(Time(day, 10, 1), player.Id);

        var entry = (await factory.EntryAsync(player.Id, day, Ct))!;
        Assert.Equal((LeagueEntryStatus.NotCompeting, LeagueEntryReason.RecentlyCompeted), (entry.Status, entry.Reason));
        Assert.Equal(expected, entry.PieceIds);
        Assert.DoesNotContain(outfit.ItemIds[2], entry.PieceIds);
    }
}
