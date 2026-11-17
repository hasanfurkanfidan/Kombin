using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Kombin.Api.Persistence.Entities.League;
using Microsoft.EntityFrameworkCore;
using static Kombin.Api.IntegrationTests.LeagueClientExtensions;

namespace Kombin.Api.IntegrationTests;

/// <summary>
/// Anonymity is the league's first acceptance criterion (<c>docs/design/league.md</c>, section 4): what player B is shown of player A's outfit
/// carries nothing that is A's (user ID, display name, e-mail, entry ID, outfit ID, wardrobe item IDs, wardrobe storage keys), and the same piece
/// on two days has two different image URLs and two different files (content hash; the store's ETag is the MD5 of a single PUT's body), so nothing
/// a machine compares links A's outfits across days, or a league outfit to the wardrobe thumbnail a Social post tags. Later PRs add the results
/// and the table here.
/// </summary>
public sealed class LeagueAnonymityTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PairsAndVotesCarryNothingOfTheOtherPlayerAndAPieceLooksNewEveryDay()
    {
        var first = NewDay();
        var second = first.AddDays(1);

        // Player A: a recognisable name and e-mail, an outfit worn on two days in a row.
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

        using var voter = await factory.CreatePlayerAsync(first, Ct);
        var bodies = new List<string>();
        var urlsByDay = new Dictionary<DateOnly, List<string>>();
        var keysByDay = new Dictionary<DateOnly, List<string>>();

        foreach (var day in new[] { first, second })
        {
            ownerClient.At(Time(day, 9));
            await ownerClient.WearOutfitAsync(outfit.OutfitId, Ct);
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
        }

        var entryIds = await factory.WithDbAsync(db => db.LeagueEntries.Where(entry => entry.UserId == ownerId).Select(entry => entry.Id).ToListAsync(Ct));
        Assert.Equal(2, entryIds.Count);
        string[] secrets =
        [
            ownerId.ToString(), ownerId.ToString("N"), email, displayName, outfit.OutfitId.ToString(), "wardrobe/",
            .. entryIds.Select(id => id.ToString()),
            .. outfit.ItemIds.Select(id => id.ToString()),
            .. outfit.ItemIds.Select(id => id.ToString("N")),
            .. outfit.ThumbKeys,
        ];

        foreach (var body in bodies)
        {
            foreach (var secret in secrets)
            {
                Assert.False(body.Contains(secret, StringComparison.OrdinalIgnoreCase), $"player B was shown player A's '{secret}': {body}");
            }
        }

        // The same three pieces on two days: six different URLs, none of them the owner's own thumbnail.
        Assert.Empty(urlsByDay[first].Intersect(urlsByDay[second]));
        Assert.Equal(6, urlsByDay.Values.SelectMany(urls => urls).Distinct().Count());
        Assert.DoesNotContain(urlsByDay.Values.SelectMany(urls => urls), url => outfit.ThumbKeys.Any(key => url.Contains(key, StringComparison.Ordinal)));

        // Six different files too: no copy has the content hash (hence the ETag) of another copy or of the wardrobe thumbnail.
        var copyHashes = keysByDay.Values.SelectMany(keys => keys).Select(key => Convert.ToHexString(SHA256.HashData(factory.Storage.Find(key)!.Content))).ToList();
        var sourceHashes = outfit.ThumbKeys.Select(key => Convert.ToHexString(SHA256.HashData(factory.Storage.Find(key)!.Content))).ToList();
        Assert.Equal(6, copyHashes.Distinct().Count());
        Assert.Empty(copyHashes.Intersect(sourceHashes));
    }
}
