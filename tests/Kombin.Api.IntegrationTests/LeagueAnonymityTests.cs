using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Kombin.Api.Features.League;
using Kombin.Api.Features.Recommendation;
using Kombin.Api.Persistence.Entities.League;
using Kombin.Api.Persistence.Entities.Recommendation;
using Microsoft.EntityFrameworkCore;
using static Kombin.Api.IntegrationTests.LeagueClientExtensions;

namespace Kombin.Api.IntegrationTests;

/// <summary>
/// Anonymity is the league's first acceptance criterion (<c>docs/design/league.md</c>, section 4): what player B is shown of player A's outfit
/// carries nothing that is A's (user ID, display name, e-mail, entry ID, outfit ID, wardrobe item IDs, wardrobe storage keys), and the same piece
/// on two days has two different image URLs and two different files (content hash; the store's ETag is the MD5 of a single PUT's body), so nothing
/// a machine compares links A's outfits across days, or a league outfit to the wardrobe thumbnail a Social post tags. B's results carry nothing
/// of A either. (The pixels are A's: someone who knows the piece can still tell, and the consent says so.) The weekly table comes in PR 4.
/// Day one is closed before day two, as in production, so the repeat rule reads it: day two is another outfit that shares one piece with day
/// one's (two pieces differ, so it competes).
/// </summary>
[Collection(LeagueCloses.Name)]
public sealed class LeagueAnonymityTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PairsAndVotesCarryNothingOfTheOtherPlayerAndAPieceLooksNewEveryDay()
    {
        var first = NewDay();
        var second = first.AddDays(1);

        // Player A: a recognisable name and e-mail; on day one an outfit, on day two another one with the same top.
        var email = ApiClientExtensions.NewEmail();
        var displayName = $"Gizli Oyuncu {Guid.NewGuid():N}"[..24];
        using var ownerClient = factory.CreateAnonymousClient();
        using (var registered = await ownerClient.RegisterAsync(email, displayName: displayName, cancellationToken: Ct))
        {
            Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
            ownerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await registered.ReadTokensAsync(Ct)).AccessToken);
        }

        var ownerId = (await ownerClient.GetJsonAsync("/api/v1/me", Ct)).GetProperty("id").GetGuid();
        ownerClient.At(Time(first, 8));
        await ownerClient.JoinLeagueOkAsync(Ct);
        var outfit = await factory.SeedOutfitAsync(ownerId, Ct);
        var others = await factory.SeedOutfitAsync(ownerId, [new LeaguePiece(Outfit3[1]), new LeaguePiece(Outfit3[2])], cancellationToken: Ct);
        var sameTop = await ComposeOutfitAsync(ownerId, [outfit.ItemIds[0], .. others.ItemIds]);
        var outfits = new Dictionary<DateOnly, Guid> { [first] = outfit.OutfitId, [second] = sameTop };

        using var voter = await factory.CreatePlayerAsync(first, Ct);
        await factory.InsertEntryAsync(first, voter.Id, cancellationToken: Ct);
        var bodies = new List<string>();
        var urlsByDay = new Dictionary<DateOnly, List<string>>();
        var keysByDay = new Dictionary<DateOnly, List<string>>();

        foreach (var day in new[] { first, second })
        {
            ownerClient.At(Time(day, 9));
            await ownerClient.WearOutfitAsync(outfits[day], Ct);
            await factory.RunLeagueEntriesAsync(Time(day, 9, 1), [ownerId], Ct);
            await factory.InsertEngineEntriesAsync(day, 2, Ct);

            voter.Client.At(Time(day, 12));
            using var nextResponse = await voter.Client.GetAsync(NextPair, Ct);
            var next = await nextResponse.Content.ReadAsStringAsync(Ct);
            Assert.True(nextResponse.IsSuccessStatusCode, next);
            var pair = System.Text.Json.JsonDocument.Parse(next).RootElement.Pair();
            voter.Client.Later(TimeSpan.FromSeconds(3));
            using var voteResponse = await voter.Client.VoteAsync(pair.GetProperty("id").GetGuid(), "left", cancellationToken: Ct);
            using var me = await voter.Client.GetAsync(Me, Ct);

            bodies.Add(next);
            bodies.Add(await voteResponse.Content.ReadAsStringAsync(Ct));
            bodies.Add(await me.Content.ReadAsStringAsync(Ct));

            var entry = (await factory.EntryAsync(ownerId, day, Ct))!;
            Assert.Equal(LeagueEntryStatus.Competing, entry.Status);
            var ownerUrls = entry.Pieces.Select(piece => factory.Storage.GetPublicUrl(piece.ImageKey!)).ToList();
            Assert.Contains(ownerUrls[0], pair.Images("left").Concat(pair.Images("right")));
            urlsByDay[day] = ownerUrls;
            keysByDay[day] = [.. entry.Pieces.Select(piece => piece.ImageKey!)];

            if (day == first)
            {
                Assert.Equal(DayCloseOutcome.Closed, (await factory.CloseDayAsync(first, Time(first, 21, 1), Ct)).Outcome);
                using var results = await voter.Client.ResultsAsync(first, Ct);
                using var latest = await voter.Client.GetAsync("/api/v1/league/results/latest", Ct);
                Assert.Equal(HttpStatusCode.OK, results.StatusCode);
                bodies.Add(await results.Content.ReadAsStringAsync(Ct));
                bodies.Add(await latest.Content.ReadAsStringAsync(Ct));
            }
        }

        var entryIds = await factory.WithDbAsync(db => db.LeagueEntries.Where(entry => entry.UserId == ownerId).Select(entry => entry.Id).ToListAsync(Ct));
        Assert.Equal(2, entryIds.Count);
        string[] secrets =
        [
            ownerId.ToString(), ownerId.ToString("N"), email, displayName, outfit.OutfitId.ToString(), sameTop.ToString(), "wardrobe/",
            .. entryIds.Select(id => id.ToString()),
            .. outfit.ItemIds.Concat(others.ItemIds).Select(id => id.ToString()),
            .. outfit.ItemIds.Concat(others.ItemIds).Select(id => id.ToString("N")),
            .. outfit.ThumbKeys,
            .. others.ThumbKeys,
        ];

        foreach (var body in bodies)
        {
            foreach (var secret in secrets)
            {
                Assert.False(body.Contains(secret, StringComparison.OrdinalIgnoreCase), $"player B was shown player A's '{secret}': {body}");
            }
        }

        // Six copies over two days, the shared top among them twice: six different URLs, none of them the owner's own thumbnail.
        Assert.NotEqual(urlsByDay[first][0], urlsByDay[second][0]);
        Assert.Equal(6, urlsByDay.Values.SelectMany(urls => urls).Distinct().Count());
        Assert.DoesNotContain(urlsByDay.Values.SelectMany(urls => urls), url => outfit.ThumbKeys.Concat(others.ThumbKeys).Any(key => url.Contains(key, StringComparison.Ordinal)));

        // Six different files too: no copy has the content hash (hence the ETag) of another copy or of the wardrobe thumbnail.
        var copyHashes = keysByDay.Values.SelectMany(keys => keys).Select(key => Convert.ToHexString(SHA256.HashData(factory.Storage.Find(key)!.Content))).ToList();
        var sourceHashes = outfit.ThumbKeys.Concat(others.ThumbKeys).Select(key => Convert.ToHexString(SHA256.HashData(factory.Storage.Find(key)!.Content))).ToList();
        Assert.Equal(6, copyHashes.Distinct().Count());
        Assert.Empty(copyHashes.Intersect(sourceHashes));
    }

    /// <summary>
    /// The weekly table (task L5): player B sees player A only as a row <c>{ rank, points, isMe, alias }</c>: no user ID, name, e-mail or entry ID,
    /// nothing that names a day or an outfit, so a row cannot be matched to an entry of the day. A's alias is new the next week.
    /// </summary>
    [Fact]
    public async Task TheTableShowsOnlyAWeeklyAliasThatChangesEveryWeek()
    {
        var monday = NewMonday();
        var email = ApiClientExtensions.NewEmail();
        var displayName = $"Tablo Oyuncusu {Guid.NewGuid():N}"[..24];
        using var ownerClient = factory.CreateAnonymousClient();
        using (var registered = await ownerClient.RegisterAsync(email, displayName: displayName, cancellationToken: Ct))
        {
            ownerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await registered.ReadTokensAsync(Ct)).AccessToken);
        }

        var ownerId = (await ownerClient.GetJsonAsync("/api/v1/me", Ct)).GetProperty("id").GetGuid();
        ownerClient.At(Time(monday, 12));
        await ownerClient.JoinLeagueOkAsync(Ct);
        var entryId = await factory.InsertEntryAsync(monday, ownerId, cancellationToken: Ct);
        using var viewer = await factory.CreatePlayerAsync(monday, Ct);
        await factory.InsertWeekScoresAsync(ownerId, monday, 60);
        await factory.InsertWeekScoresAsync(viewer.Id, monday, 40);

        var bodies = new List<string> { (await viewer.Client.At(Time(monday.AddDays(2), 12)).TableOkAsync(Ct)).GetRawText() };
        await factory.CloseDaysDirectlyAsync(monday, 7, Ct);
        await factory.RunJobAsync<WeekCloser, WeekCloseResult>(closer => closer.CloseWeekAsync(monday, Time(monday.AddDays(6), 21, 5), [ownerId, viewer.Id], Ct));
        bodies.Add((await viewer.Client.At(Time(monday.AddDays(6), 22)).TableOkAsync(Ct)).GetRawText());
        bodies.Add((await viewer.Client.At(Time(monday.AddDays(7), 12)).TableOkAsync(Ct)).GetRawText());
        bodies.Add((await viewer.Client.GetJsonAsync(Me, Ct)).GetRawText());

        string[] secrets = [ownerId.ToString(), ownerId.ToString("N"), email, displayName, entryId.ToString()];
        foreach (var body in bodies)
        {
            Assert.All(secrets, secret => Assert.False(body.Contains(secret, StringComparison.OrdinalIgnoreCase), $"player B was shown '{secret}': {body}"));
            if (JsonDocument.Parse(body).RootElement.TryGetProperty("rows", out var rows))
            {
                // Nothing in a table names a day or an outfit.
                Assert.DoesNotContain("\"day\"", body, StringComparison.Ordinal);
                Assert.DoesNotContain("entry", body, StringComparison.OrdinalIgnoreCase);
                Assert.All(rows.EnumerateArray(), row => Assert.Equal(["alias", "isMe", "points", "rank"], row.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal)));
            }
        }

        var aliases = await factory.WithDbAsync(db => db.LeagueGroupMembers.AsNoTracking().Where(member => member.UserId == ownerId).OrderBy(member => member.WeekStart).Select(member => member.Alias).ToListAsync(Ct));
        Assert.Equal(2, aliases.Count);
        Assert.NotEqual(aliases[0], aliases[1]);
        Assert.Contains(aliases[0], bodies[1], StringComparison.Ordinal);
        Assert.Contains(aliases[1], bodies[2], StringComparison.Ordinal);
    }

    /// <summary>A worn-able outfit of the owner's existing wardrobe items (top, bottom, shoes in that order), as the engine would store it.</summary>
    private Task<Guid> ComposeOutfitAsync(Guid userId, IReadOnlyList<Guid> itemIds) => factory.WithDbAsync(async db =>
    {
        var outfit = new Outfit
        {
            UserId = userId,
            Kind = OutfitKind.Daily,
            Completeness = OutfitCompleteness.Complete,
            ForDate = new DateOnly(2040, 1, 1),
            Rule = "league",
            Hash = Guid.NewGuid().ToString("N")[..32],
            Status = OutfitStatus.Suggested,
            Narration = "Lig",
            NarrationInput = "{}",
        };
        outfit.Items.AddRange(itemIds.Select((itemId, position) => new OutfitItem
        {
            Slot = OutfitSlots.ForCategorySlug(Outfit3[position]),
            Role = OutfitItemRole.Wardrobe,
            WardrobeItemId = itemId,
            Position = position,
        }));
        db.Outfits.Add(outfit);
        await db.SaveChangesAsync(Ct);

        return outfit.Id;
    });
}
