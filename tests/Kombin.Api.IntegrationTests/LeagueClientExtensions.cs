using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Kombin.Api.Common.Storage;
using Kombin.Api.Features.League;
using Kombin.Api.Features.League.Jobs;
using Kombin.Api.Features.Recommendation;
using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.League;
using Kombin.Api.Persistence.Entities.Recommendation;
using Kombin.Api.Persistence.Entities.Social;
using Kombin.Api.Persistence.Entities.Wardrobe;
using Microsoft.EntityFrameworkCore;

namespace Kombin.Api.IntegrationTests;

/// <summary>A wardrobe piece of a seeded outfit: its category, the safety filter's verdict (null: not answered yet) and the cutout's flavour.</summary>
internal sealed record LeaguePiece(string CategorySlug, ModerationVerdict? Verdict = ModerationVerdict.Allow, string Cutout = "ok", bool Archived = false);

/// <summary>A seeded outfit: its ID, its wardrobe items and their stored thumbnails (the owner's keys).</summary>
internal sealed record SeededOutfit(Guid OutfitId, IReadOnlyList<Guid> ItemIds, IReadOnlyList<string> ThumbKeys);

/// <summary>
/// Kombin Ligi in the integration tests. Every test plays <b>its own league day</b> (<see cref="NewDay"/>), far in the future and three days
/// from any other test's, and sends its time with <see cref="At"/> (the test clock's <c>X-Test-Now</c>): the pairing pool and the engine fill
/// are per day, so tests never see each other's outfits, and no test depends on the hour it runs at.
/// </summary>
internal static class LeagueClientExtensions
{
    public const string Me = "/api/v1/league/me";

    public const string NextPair = "/api/v1/league/pairs/next";

    private static int NextDayIndex = Random.Shared.Next(0, 3000);

    public static readonly string[] Outfit3 = ["ust-giyim/gomlek", "alt-giyim/pantolon", "ayakkabi/sneaker"];

    /// <summary>A day of the test's own.</summary>
    public static DateOnly NewDay() => new DateOnly(2040, 1, 1).AddDays(Interlocked.Increment(ref NextDayIndex) * 3);

    /// <summary>An instant of <paramref name="day"/> at the given Istanbul time.</summary>
    public static DateTimeOffset Time(DateOnly day, int hour, int minute = 0, int second = 0, int millisecond = 0) =>
        LeagueDays.ClosesAt(day, new TimeOnly(hour, minute, second, millisecond));

    /// <summary>Every later request of this client runs at <paramref name="now"/>.</summary>
    public static HttpClient At(this HttpClient client, DateTimeOffset now)
    {
        client.DefaultRequestHeaders.Remove(TestHeaderClock.Header);
        client.DefaultRequestHeaders.Add(TestHeaderClock.Header, now.ToString("O", CultureInfo.InvariantCulture));

        return client;
    }

    /// <summary>Moves this client's time on by <paramref name="by"/> (a voter who looked at the pair for a while).</summary>
    public static HttpClient Later(this HttpClient client, TimeSpan by)
    {
        var now = client.DefaultRequestHeaders.TryGetValues(TestHeaderClock.Header, out var values)
            ? DateTimeOffset.Parse(values.Single(), CultureInfo.InvariantCulture)
            : DateTimeOffset.UtcNow;

        return client.At(now + by);
    }

    /// <summary>Serves the next pair, then lets two seconds pass, so a vote on it is not too fast.</summary>
    public static async Task<Guid> ServeAndLookAsync(this HttpClient client, CancellationToken cancellationToken)
    {
        var pair = (await client.NextPairAsync(cancellationToken)).Pair().GetProperty("id").GetGuid();
        client.Later(TimeSpan.FromSeconds(2));

        return pair;
    }

    public static Task<HttpResponseMessage> JoinLeagueAsync(this HttpClient client, string? consentVersion, CancellationToken cancellationToken) =>
        client.PostAsJsonAsync("/api/v1/league/join", new { consentVersion }, cancellationToken);

    /// <summary>Joins with the current consent version, asserts a 200 and returns the league state.</summary>
    public static async Task<JsonElement> JoinLeagueOkAsync(this HttpClient client, CancellationToken cancellationToken)
    {
        using var response = await client.JoinLeagueAsync(LeagueConsent.Current.Version, cancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"POST /league/join returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(cancellationToken)}");

        return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
    }

    public static Task<HttpResponseMessage> LeaveLeagueAsync(this HttpClient client, CancellationToken cancellationToken) =>
        client.PostAsync("/api/v1/league/leave", null, cancellationToken);

    public static Task<HttpResponseMessage> VoteAsync(this HttpClient client, Guid pairId, string choice, int decisionMs = 1500, CancellationToken cancellationToken = default) =>
        client.PostAsJsonAsync($"/api/v1/league/pairs/{pairId}/vote", new { choice, decisionMs }, cancellationToken);

    public static async Task<JsonElement> VoteOkAsync(this HttpClient client, Guid pairId, string choice, int decisionMs = 1500, CancellationToken cancellationToken = default)
    {
        using var response = await client.VoteAsync(pairId, choice, decisionMs, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"vote returned {(int)response.StatusCode}: {body}");

        return JsonDocument.Parse(body).RootElement.Clone();
    }

    public static Task<HttpResponseMessage> ReportOutfitAsync(this HttpClient client, Guid pairId, string side, string? reason = null, CancellationToken cancellationToken = default) =>
        client.PostAsJsonAsync($"/api/v1/league/pairs/{pairId}/report", new { side, reason }, cancellationToken);

    /// <summary><c>GET /league/pairs/next</c>, asserting a 200.</summary>
    public static Task<JsonElement> NextPairAsync(this HttpClient client, CancellationToken cancellationToken) => client.GetJsonAsync(NextPair, cancellationToken);

    /// <summary>The served pair of a next-pair answer (asserting there is one).</summary>
    public static JsonElement Pair(this JsonElement next)
    {
        var pair = next.GetProperty("pair");
        Assert.True(pair.ValueKind == JsonValueKind.Object, $"no pair: {next.GetRawText()}");

        return pair;
    }

    /// <summary>The image URLs of one side of a pair.</summary>
    public static List<string> Images(this JsonElement pair, string side) =>
        [.. pair.GetProperty(side).GetProperty("pieces").EnumerateArray().Select(piece => piece.GetProperty("imageUrl").GetString()!)];

    /// <summary>
    /// Inserts ready wardrobe items (thumbnail and cutout in the fake store, the given verdict) and a suggested outfit of them, as the engine
    /// would have stored it. <paramref name="gapProductId"/> adds a catalog gap piece (something the player does not own).
    /// </summary>
    public static Task<SeededOutfit> SeedOutfitAsync(
        this ApiFactory factory, Guid userId, IReadOnlyList<LeaguePiece> pieces, Occasion? occasion = null, Guid? gapProductId = null, CancellationToken cancellationToken = default) =>
        factory.WithDbAsync(async db =>
        {
            var categories = await db.Categories.ToDictionaryAsync(category => category.Slug, category => category.Id, cancellationToken);
            var items = new List<WardrobeItem>();
            var keys = new List<string>();

            foreach (var piece in pieces)
            {
                var item = new WardrobeItem
                {
                    UserId = userId,
                    CategoryId = categories[piece.CategorySlug],
                    PrimaryColor = ProductColor.Navy,
                    Status = WardrobeItemStatus.Ready,
                    ModerationVerdict = piece.Verdict,
                    IsArchived = piece.Archived,
                    ConfirmedAt = DateTimeOffset.UtcNow,
                };
                item.ImageThumbKey = StorageKeys.Build("wardrobe", userId, item.Id, ImageVariant.Thumb, "webp");
                item.ImageCutoutKey = StorageKeys.Build("wardrobe", userId, item.Id, ImageVariant.Cutout, "png");
                factory.Storage.Upload(item.ImageThumbKey, Encoding.UTF8.GetBytes($"thumb:{item.Id:N}"), "image/webp");
                factory.Storage.Upload(item.ImageCutoutKey, FakeVisionClient.CutoutFor(piece.Cutout), "image/png");
                items.Add(item);
                keys.Add(item.ImageThumbKey);
            }

            var outfit = new Outfit
            {
                UserId = userId,
                Kind = OutfitKind.Daily,
                Completeness = gapProductId is null ? OutfitCompleteness.Complete : OutfitCompleteness.NeedsOne,
                Occasion = occasion,
                ForDate = new DateOnly(2040, 1, 1),
                Rule = "league",
                Hash = Guid.NewGuid().ToString("N")[..32],
                Status = OutfitStatus.Suggested,
                Narration = "Lig",
                NarrationInput = "{}",
            };
            outfit.Items.AddRange(items.Select((item, position) => new OutfitItem
            {
                Slot = OutfitSlots.ForCategorySlug(pieces[position].CategorySlug),
                Role = OutfitItemRole.Wardrobe,
                WardrobeItemId = item.Id,
                Position = position,
            }));

            if (gapProductId is { } productId)
            {
                outfit.Items.Add(new OutfitItem { Slot = OutfitSlot.Outerwear, Role = OutfitItemRole.Gap, ProductId = productId, Position = items.Count });
            }

            db.WardrobeItems.AddRange(items);
            db.Outfits.Add(outfit);
            await db.SaveChangesAsync(cancellationToken);

            return new SeededOutfit(outfit.Id, [.. items.Select(item => item.Id)], keys);
        });

    public static Task<SeededOutfit> SeedOutfitAsync(this ApiFactory factory, Guid userId, CancellationToken cancellationToken) =>
        factory.SeedOutfitAsync(userId, [.. Outfit3.Select(slug => new LeaguePiece(slug))], cancellationToken: cancellationToken);

    /// <summary>"Bugün giydim" through the outfit engine's own endpoint (at the client's time).</summary>
    public static async Task WearOutfitAsync(this HttpClient client, Guid outfitId, CancellationToken cancellationToken)
    {
        using var response = await client.PostAsync($"/api/v1/me/outfits/{outfitId}/worn", null, cancellationToken);
        Assert.True(response.IsSuccessStatusCode, $"worn returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(cancellationToken)}");
    }

    public static Task<LeagueEntriesSummary> RunLeagueEntriesAsync(this ApiFactory factory, DateTimeOffset now, IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken) =>
        factory.RunJobAsync<LeagueEntriesJob, LeagueEntriesSummary>(job => job.ProcessAsync(now, userIds, cancellationToken));

    public static Task<LeagueEntry?> EntryAsync(this ApiFactory factory, Guid userId, DateOnly day, CancellationToken cancellationToken) =>
        factory.WithDbAsync(db => db.LeagueEntries.AsNoTracking().Include(entry => entry.Pieces).SingleOrDefaultAsync(entry => entry.UserId == userId && entry.Day == day, cancellationToken));

    /// <summary>
    /// Inserts competing entries directly (the pool a voter is shown), with images that look like the league's: a copy key for a player's
    /// entry, a catalog URL for an engine one. <paramref name="userId"/> null is an engine entry.
    /// </summary>
    public static Task<Guid> InsertEntryAsync(
        this ApiFactory factory,
        DateOnly day,
        Guid? userId,
        Audience audience = Audience.Women,
        Occasion? occasion = Occasion.Office,
        int exposures = 0,
        CancellationToken cancellationToken = default) =>
        factory.WithDbAsync(async db =>
        {
            var pieceIds = new[] { Guid.NewGuid(), Guid.NewGuid() }.Order().ToArray();
            var entry = new LeagueEntry
            {
                Day = day,
                UserId = userId,
                Kind = userId is null ? LeagueEntryKind.Engine : LeagueEntryKind.Human,
                Audience = audience,
                Occasion = occasion,
                PieceIds = pieceIds,
                PieceSetHash = Guid.NewGuid().ToString("N"),
                Status = LeagueEntryStatus.Competing,
                Exposures = exposures,
            };
            entry.Pieces.AddRange(new[] { OutfitSlot.Top, OutfitSlot.Bottom }.Select((slot, position) => new LeagueEntryPiece
            {
                Position = (short)position,
                Slot = slot,
                ImageKey = userId is null ? null : $"league/{Guid.NewGuid():D}/{Guid.NewGuid():D}/thumb.webp",
                ImageUrl = userId is null ? $"https://img.example.com/engine/{Guid.NewGuid():N}.jpg" : null,
            }));
            db.LeagueEntries.Add(entry);
            await db.SaveChangesAsync(cancellationToken);

            return entry.Id;
        });

    public static Task<int> InsertEngineEntriesAsync(this ApiFactory factory, DateOnly day, int count, CancellationToken cancellationToken) =>
        Enumerable.Range(0, count).Aggregate(Task.FromResult(0), async (previous, _) =>
        {
            await previous;
            await factory.InsertEntryAsync(day, null, cancellationToken: cancellationToken);
            return count;
        });

    /// <summary>A joined player whose requests run at noon of <paramref name="day"/>.</summary>
    public static async Task<TestUser> CreatePlayerAsync(this ApiFactory factory, DateOnly day, CancellationToken cancellationToken)
    {
        var user = await factory.CreateUserAsync(cancellationToken);
        user.Client.At(Time(day, 12));
        await user.Client.JoinLeagueOkAsync(cancellationToken);

        return user;
    }
}
