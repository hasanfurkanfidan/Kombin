using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Kombin.Api.Common.Storage;
using Kombin.Api.Persistence.Entities.AskFriend;
using Kombin.Api.Persistence.Entities.Social;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using static Kombin.Api.IntegrationTests.LeagueClientExtensions;

namespace Kombin.Api.IntegrationTests;

/// <summary>"Arkadaşına sor", PR AF2: the asker's API (<c>docs/design/ask-a-friend.md</c>, sections 2-5), written from the design note.</summary>
public sealed partial class AskFriendTests(ApiFactory factory)
{
    private const string Asks = "/api/v1/me/asks";

    private const string ConsentVersion = "2026-11-v1";

    private const string ConsentText =
        "Linki alan herkes, giriş yapmadan bu kombinlerdeki parçaların fotoğraflarını görebilir ve cevap verebilir. Adın ve profilin sayfada "
        + "görünmez. Link 24 saat sonra kendiliğinden kapanır; istersen daha önce kapatabilirsin. Link kapanınca fotoğraflar silinir. Linki "
        + "paylaşarak 18 yaşından büyük olduğunu onaylarsın.";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [GeneratedRegex("^https://api\\.kombin\\.test/sor/[A-Za-z0-9_-]{22}$")]
    private static partial Regex LinkPattern();

    private static Task<HttpResponseMessage> AskAsync(HttpClient client, IEnumerable<Guid> outfitIds, string? consentVersion = ConsentVersion) =>
        client.PostAsJsonAsync(Asks, new { outfitIds = outfitIds.ToArray(), consentVersion }, Ct);

    private static async Task<JsonElement> AskOkAsync(HttpClient client, params Guid[] outfitIds)
    {
        using var response = await AskAsync(client, outfitIds);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"create returned {(int)response.StatusCode}: {body}");

        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response) =>
        (await response.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString();

    private Task<List<AskPiece>> PiecesAsync(Guid askId) =>
        factory.WithDbAsync(db => db.AskPieces.AsNoTracking().Where(piece => piece.AskId == askId).OrderBy(piece => piece.Option).ThenBy(piece => piece.Position).ToListAsync(Ct));

    private async Task<List<string>> AskKeysInStorageAsync()
    {
        var keys = new List<string>();
        await foreach (var info in factory.Storage.ListAsync("ask/", Ct))
        {
            keys.Add(info.Key);
        }

        return keys;
    }

    [Fact]
    public async Task CreateAskForOneOwnOutfitReturnsALink()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var outfit = await factory.SeedOutfitAsync(user.Id, Ct);

        using var response = await AskAsync(user.Client, [outfit.OutfitId]);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var ask = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement;
        var id = ask.GetProperty("id").GetGuid();
        Assert.Equal($"/api/v1/me/asks/{id}", response.Headers.Location?.OriginalString);
        Assert.Equal("Single", ask.GetProperty("kind").GetString());
        Assert.Equal("Open", ask.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, ask.GetProperty("statusMessage").ValueKind);
        Assert.Matches(LinkPattern(), ask.GetProperty("url").GetString());
        Assert.Equal("Bu kombin olur mu? Bir dokunuşla cevap ver:", ask.GetProperty("shareText").GetString());
        Assert.Equal(ask.GetProperty("createdAt").GetDateTimeOffset().AddHours(24), ask.GetProperty("expiresAt").GetDateTimeOffset());

        var option = Assert.Single(ask.GetProperty("options").EnumerateArray());
        Assert.Equal("A", option.GetProperty("option").GetString());
        Assert.Equal(outfit.OutfitId, option.GetProperty("outfitId").GetGuid());
        Assert.Equal(3, option.GetProperty("pieces").GetArrayLength());

        var tally = ask.GetProperty("tally");
        Assert.Equal((0, 0, 0, 0, 0), (tally.GetProperty("yes").GetInt32(), tally.GetProperty("no").GetInt32(), tally.GetProperty("a").GetInt32(), tally.GetProperty("b").GetInt32(), tally.GetProperty("total").GetInt32()));
        Assert.Equal(0, ask.GetProperty("answers").GetArrayLength());

        // The token in the URL is the stored one (22 characters, 128 bits).
        var stored = await factory.WithDbAsync(db => db.Asks.AsNoTracking().SingleAsync(row => row.Id == id, Ct));
        Assert.EndsWith("/sor/" + stored.Token, ask.GetProperty("url").GetString(), StringComparison.Ordinal);

        var again = await user.Client.GetJsonAsync($"{Asks}/{id}", Ct);
        Assert.Equal(ask.GetProperty("url").GetString(), again.GetProperty("url").GetString());
    }

    [Fact]
    public async Task CreateAskWithTwoOutfitsIsAPair()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var first = await factory.SeedOutfitAsync(user.Id, Ct);
        var second = await factory.SeedOutfitAsync(user.Id, Ct);

        var ask = await AskOkAsync(user.Client, first.OutfitId, second.OutfitId);

        Assert.Equal("Pair", ask.GetProperty("kind").GetString());
        Assert.Equal("Bugün hangisini giyeyim? Bir dokunuşla cevap ver:", ask.GetProperty("shareText").GetString());
        var options = ask.GetProperty("options").EnumerateArray().ToList();
        Assert.Equal("A,B", string.Join(",", options.Select(option => option.GetProperty("option").GetString())));
        Assert.Equal(new[] { first.OutfitId, second.OutfitId }, options.Select(option => option.GetProperty("outfitId").GetGuid()));
    }

    [Fact]
    public async Task CreateAskForAnotherUsersOutfitIs404()
    {
        using var owner = await factory.CreateUserAsync(Ct);
        using var other = await factory.CreateUserAsync(Ct);
        var outfit = await factory.SeedOutfitAsync(owner.Id, Ct);

        using var response = await AskAsync(other.Client, [outfit.OutfitId]);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("AskFriend.OutfitNotFound", await ErrorCodeAsync(response));
        Assert.Equal(0, await factory.WithDbAsync(db => db.Asks.CountAsync(ask => ask.UserId == other.Id, Ct)));
    }

    [Fact]
    public async Task CreateAskWithAnOldConsentVersionIs409()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var outfit = await factory.SeedOutfitAsync(user.Id, Ct);

        using var response = await AskAsync(user.Client, [outfit.OutfitId], "2025-01-v0");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("AskFriend.ConsentRequired", await ErrorCodeAsync(response));
        Assert.Equal(0, await factory.WithDbAsync(db => db.AskConsents.CountAsync(consent => consent.UserId == user.Id, Ct)));
    }

    [Fact]
    public async Task FirstAskStoresTheConsentTextOnce()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var outfit = await factory.SeedOutfitAsync(user.Id, Ct);

        var before = await user.Client.GetJsonAsync($"{Asks}/consent", Ct);
        Assert.Equal(ConsentVersion, before.GetProperty("version").GetString());
        Assert.Equal(ConsentText, before.GetProperty("text").GetString());
        Assert.False(before.GetProperty("accepted").GetBoolean());

        await AskOkAsync(user.Client, outfit.OutfitId);
        await AskOkAsync(user.Client, outfit.OutfitId);

        var consent = Assert.Single(await factory.WithDbAsync(db => db.AskConsents.AsNoTracking().Where(row => row.UserId == user.Id).ToListAsync(Ct)));
        Assert.Equal(ConsentVersion, consent.Version);
        Assert.Equal(ConsentText, consent.Text);
        Assert.True((await user.Client.GetJsonAsync($"{Asks}/consent", Ct)).GetProperty("accepted").GetBoolean());
    }

    [Fact]
    public async Task CreateAskForAKnownMinorIs403()
    {
        using var minor = await factory.CreateUserAsync(Ct);
        using var unknown = await factory.CreateUserAsync(Ct);
        using (var profile = await minor.Client.PutAsJsonAsync("/api/v1/me/profile", new { birthYear = DateTime.UtcNow.Year - 16 }, Ct))
        {
            Assert.Equal(HttpStatusCode.OK, profile.StatusCode);
        }

        var minorsOutfit = await factory.SeedOutfitAsync(minor.Id, Ct);
        var unknownsOutfit = await factory.SeedOutfitAsync(unknown.Id, Ct);

        using var refused = await AskAsync(minor.Client, [minorsOutfit.OutfitId]);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("AskFriend.AgeRestricted", await ErrorCodeAsync(refused));

        // An unknown birth year may ask: the consent is the declaration.
        await AskOkAsync(unknown.Client, unknownsOutfit.OutfitId);
    }

    [Fact]
    public async Task UnmoderatedPieceHasNoImage()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var outfit = await factory.SeedOutfitAsync(
            user.Id,
            [
                new LeaguePiece("ust-giyim/gomlek", ModerationVerdict.Allow),
                new LeaguePiece("alt-giyim/pantolon", ModerationVerdict.Review),
                new LeaguePiece("ayakkabi/sneaker", Verdict: null),
            ],
            cancellationToken: Ct);

        var ask = await AskOkAsync(user.Client, outfit.OutfitId);

        var pieces = await PiecesAsync(ask.GetProperty("id").GetGuid());
        Assert.Equal(3, pieces.Count);
        Assert.NotNull(pieces.Single(piece => piece.WardrobeItemId == outfit.ItemIds[0]).CopyKey);
        Assert.Null(pieces.Single(piece => piece.WardrobeItemId == outfit.ItemIds[1]).CopyKey);
        Assert.Null(pieces.Single(piece => piece.WardrobeItemId == outfit.ItemIds[2]).CopyKey);
        Assert.Single(pieces, piece => piece.CopyKey is not null);
    }

    [Fact]
    public async Task OutfitWithNothingToShowIs409()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var outfit = await factory.SeedOutfitAsync(
            user.Id,
            [new LeaguePiece("ust-giyim/gomlek", ModerationVerdict.Review), new LeaguePiece("alt-giyim/pantolon", ModerationVerdict.Block)],
            cancellationToken: Ct);
        var keysBefore = await AskKeysInStorageAsync();

        using var response = await AskAsync(user.Client, [outfit.OutfitId]);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("AskFriend.NothingToShow", await ErrorCodeAsync(response));
        Assert.Equal(keysBefore, await AskKeysInStorageAsync());
        Assert.Equal(0, await factory.WithDbAsync(db => db.Asks.CountAsync(ask => ask.UserId == user.Id, Ct)));
    }

    [Fact]
    public async Task AskOverTheDailyLimitIs409()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var outfit = await factory.SeedOutfitAsync(user.Id, Ct);

        // 12:00 in Istanbul, so every ask lands in the same Istanbul day; MaxAsksPerDay is 10.
        user.Client.At(new DateTimeOffset(2041, 5, 20, 9, 0, 0, TimeSpan.Zero));
        for (var index = 0; index < 10; index++)
        {
            await AskOkAsync(user.Client, outfit.OutfitId);
            user.Client.Later(TimeSpan.FromMinutes(1));
        }

        using var response = await AskAsync(user.Client, [outfit.OutfitId]);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("AskFriend.DailyLimit", await ErrorCodeAsync(response));
        Assert.Equal(10, await factory.WithDbAsync(db => db.Asks.CountAsync(ask => ask.UserId == user.Id, Ct)));

        // The next Istanbul day starts a new count.
        user.Client.At(new DateTimeOffset(2041, 5, 20, 21, 1, 0, TimeSpan.Zero));
        await AskOkAsync(user.Client, outfit.OutfitId);
    }

    [Fact]
    public async Task CopiesAreStoredUnderKeysWithoutTheUserId()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var outfit = await factory.SeedOutfitAsync(user.Id, Ct);

        var ask = await AskOkAsync(user.Client, outfit.OutfitId);

        var keys = (await PiecesAsync(ask.GetProperty("id").GetGuid())).Select(piece => piece.CopyKey).ToList();
        Assert.Equal(3, keys.Count);
        Assert.All(keys, key =>
        {
            Assert.NotNull(key);
            Assert.StartsWith("ask/", key, StringComparison.Ordinal);
            Assert.EndsWith("/shared.webp", key, StringComparison.Ordinal);
            Assert.DoesNotContain(user.Id.ToString(), key, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(user.Id.ToString("N"), key, StringComparison.OrdinalIgnoreCase);
            Assert.All(outfit.ItemIds, itemId => Assert.DoesNotContain(itemId.ToString(), key, StringComparison.OrdinalIgnoreCase));
            Assert.False(StorageKeys.IsPublic(key));
            Assert.Equal(Thumbnail, factory.Storage.Find(key)!.Content);
        });
        Assert.Equal(3, keys.Distinct().Count());
        Assert.DoesNotContain(factory.Storage.KeysOfUser(user.Id), key => key.StartsWith("ask/", StringComparison.Ordinal));

        // The asker's own view shows their own thumbnails, not the copies.
        var pieces = ask.GetProperty("options")[0].GetProperty("pieces").EnumerateArray().Select(piece => piece.GetProperty("imageUrl").GetString()).ToList();
        Assert.Equal(outfit.ThumbKeys.Select(key => factory.Storage.GetPublicUrl(key)).Order(), pieces.Order());
    }

    [Fact]
    public async Task CloseDeletesTheCopiesAtOnce()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var outfit = await factory.SeedOutfitAsync(user.Id, Ct);
        var ask = await AskOkAsync(user.Client, outfit.OutfitId);
        var id = ask.GetProperty("id").GetGuid();
        var keys = (await PiecesAsync(id)).Select(piece => piece.CopyKey!).ToList();
        Assert.All(keys, key => Assert.NotNull(factory.Storage.Find(key)));

        using (var close = await user.Client.PostAsync($"{Asks}/{id}/close", null, Ct))
        {
            Assert.Equal(HttpStatusCode.NoContent, close.StatusCode);
        }

        Assert.All(keys, key => Assert.Null(factory.Storage.Find(key)));
        var closed = await user.Client.GetJsonAsync($"{Asks}/{id}", Ct);
        Assert.Equal("Closed", closed.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, closed.GetProperty("url").ValueKind);
        var stored = await factory.WithDbAsync(db => db.Asks.AsNoTracking().SingleAsync(row => row.Id == id, Ct));
        Assert.NotNull(stored.ClosedAt);
        Assert.NotNull(stored.CopiesDeletedAt);
    }

    [Fact]
    public async Task ClosingTwiceIs204()
    {
        using var user = await factory.CreateUserAsync(Ct);
        using var other = await factory.CreateUserAsync(Ct);
        var outfit = await factory.SeedOutfitAsync(user.Id, Ct);
        var id = (await AskOkAsync(user.Client, outfit.OutfitId)).GetProperty("id").GetGuid();

        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var close = await user.Client.PostAsync($"{Asks}/{id}/close", null, Ct);
            Assert.Equal(HttpStatusCode.NoContent, close.StatusCode);
        }

        // Someone else's ask does not exist for them.
        using var foreign = await other.Client.PostAsync($"{Asks}/{id}/close", null, Ct);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal("AskFriend.NotFound", await ErrorCodeAsync(foreign));
    }

    [Fact]
    public async Task AskListShowsOnlyTheCallersAsks()
    {
        using var user = await factory.CreateUserAsync(Ct);
        using var other = await factory.CreateUserAsync(Ct);
        var outfit = await factory.SeedOutfitAsync(user.Id, Ct);
        var othersOutfit = await factory.SeedOutfitAsync(other.Id, Ct);
        var start = new DateTimeOffset(2041, 6, 3, 7, 0, 0, TimeSpan.Zero);

        user.Client.At(start);
        var older = (await AskOkAsync(user.Client, outfit.OutfitId)).GetProperty("id").GetGuid();
        user.Client.At(start.AddMinutes(5));
        var newer = (await AskOkAsync(user.Client, outfit.OutfitId)).GetProperty("id").GetGuid();
        other.Client.At(start.AddMinutes(10));
        var foreign = (await AskOkAsync(other.Client, othersOutfit.OutfitId)).GetProperty("id").GetGuid();

        var page = await user.Client.GetJsonAsync(Asks, Ct);

        var items = page.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(new[] { newer, older }, items.Select(item => item.GetProperty("id").GetGuid()));
        Assert.DoesNotContain(foreign, items.Select(item => item.GetProperty("id").GetGuid()));
        Assert.Equal(2, page.GetProperty("totalCount").GetInt32());
        Assert.All(items, item =>
        {
            Assert.Equal("Single", item.GetProperty("kind").GetString());
            Assert.Equal("Open", item.GetProperty("status").GetString());
            Assert.Equal(0, item.GetProperty("total").GetInt32());
            Assert.Equal(JsonValueKind.Null, item.GetProperty("leading").ValueKind);
        });

        using var notMine = await user.Client.GetAsync($"{Asks}/{foreign}", Ct);
        Assert.Equal(HttpStatusCode.NotFound, notMine.StatusCode);
        Assert.Equal("AskFriend.NotFound", await ErrorCodeAsync(notMine));
    }

    [Fact]
    public async Task HidingAnAnswerDropsItFromTheTally()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var outfit = await factory.SeedOutfitAsync(user.Id, Ct);
        var id = (await AskOkAsync(user.Client, outfit.OutfitId)).GetProperty("id").GetGuid();
        var answer = new AskAnswer
        {
            AskId = id,
            VoterHash = new string('a', 64),
            Choice = AskChoice.Yes,
            Name = "Ayşe",
            Note = "Çok iyi",
            CreatedAt = TestClock.UtcNow,
            UpdatedAt = TestClock.UtcNow,
        };
        await factory.WithDbAsync(async db =>
        {
            db.AskAnswers.Add(answer);
            return await db.SaveChangesAsync(Ct);
        });

        var before = await user.Client.GetJsonAsync($"{Asks}/{id}", Ct);
        Assert.Equal((1, 1), (before.GetProperty("tally").GetProperty("yes").GetInt32(), before.GetProperty("tally").GetProperty("total").GetInt32()));
        var shown = Assert.Single(before.GetProperty("answers").EnumerateArray());
        Assert.Equal(("Yes", "Ayşe", "Çok iyi"), (shown.GetProperty("choice").GetString(), shown.GetProperty("name").GetString(), shown.GetProperty("note").GetString()));

        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var hide = await user.Client.DeleteAsync($"{Asks}/{id}/answers/{answer.Id}", Ct);
            Assert.Equal(HttpStatusCode.NoContent, hide.StatusCode);
        }

        var after = await user.Client.GetJsonAsync($"{Asks}/{id}", Ct);
        Assert.Equal((0, 0), (after.GetProperty("tally").GetProperty("yes").GetInt32(), after.GetProperty("tally").GetProperty("total").GetInt32()));
        Assert.Equal(0, after.GetProperty("answers").GetArrayLength());
        Assert.NotNull((await factory.WithDbAsync(db => db.AskAnswers.AsNoTracking().SingleAsync(row => row.Id == answer.Id, Ct))).HiddenAt);
    }

    [Fact]
    public async Task AskRoutesAre404WhileDisabled()
    {
        await using var off = factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection([new KeyValuePair<string, string?>("AskFriend:Enabled", "false")])));
        using var user = await factory.CreateUserAsync(Ct);
        var outfit = await factory.SeedOutfitAsync(user.Id, Ct);
        using var client = off.CreateClient();
        client.DefaultRequestHeaders.Authorization = user.Client.DefaultRequestHeaders.Authorization;
        var id = Guid.NewGuid();

        var requests = new Func<Task<HttpResponseMessage>>[]
        {
            () => client.GetAsync($"{Asks}/consent", Ct),
            () => client.PostAsJsonAsync(Asks, new { outfitIds = new[] { outfit.OutfitId }, consentVersion = ConsentVersion }, Ct),
            () => client.GetAsync(Asks, Ct),
            () => client.GetAsync($"{Asks}/{id}", Ct),
            () => client.PostAsync($"{Asks}/{id}/close", null, Ct),
            () => client.DeleteAsync($"{Asks}/{id}/answers/{Guid.NewGuid()}", Ct),
        };

        foreach (var send in requests)
        {
            using var response = await send();
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("AskFriend.Disabled", await ErrorCodeAsync(response));
        }

        Assert.Equal(0, await factory.WithDbAsync(db => db.Asks.CountAsync(ask => ask.UserId == user.Id, Ct)));
    }
}
