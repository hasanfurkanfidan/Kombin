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
using Npgsql;

namespace Kombin.Api.IntegrationTests;

/// <summary>
/// A wardrobe piece of a seeded outfit: its category, the safety filter's verdict (null: not answered yet), the cutout's flavour, and
/// <c>ProductId</c> for a piece added from the catalog (<c>FromCatalog</c>).
/// </summary>
internal sealed record LeaguePiece(string CategorySlug, ModerationVerdict? Verdict = ModerationVerdict.Allow, string Cutout = "ok", bool Archived = false, Guid? ProductId = null);

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

    /// <summary>A real 4 × 3 lossy WEBP (Pillow), the format the vision service stores thumbnails in: the league copies only images it can mark.</summary>
    public static readonly byte[] Thumbnail = Convert.FromBase64String("UklGRjoAAABXRUJQVlA4IC4AAACQAQCdASoEAAMAAUAmJaACdLoAA5gA/vi1F/94z/+4z/+4z/37f1qoVO/pAAAA");

    /// <summary>The marked copy without its random block: the WEBP chunks of the source, which a copy must carry unchanged.</summary>
    public static byte[] ImageData(byte[] webp)
    {
        var chunks = new List<byte>();
        for (var position = 12; position < webp.Length;)
        {
            var size = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(webp.AsSpan(position + 4));
            var type = Encoding.ASCII.GetString(webp, position, 4);
            if (type is "VP8 " or "VP8L" or "ALPH")
            {
                chunks.AddRange(webp.AsSpan(position, 8 + size).ToArray());
            }

            position += 8 + size + (size & 1);
        }

        return [.. chunks];
    }

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
                    ProductId = piece.ProductId,
                    FromCatalog = piece.ProductId is not null,
                };
                item.ImageThumbKey = StorageKeys.Build("wardrobe", userId, item.Id, ImageVariant.Thumb, "webp");
                item.ImageCutoutKey = StorageKeys.Build("wardrobe", userId, item.Id, ImageVariant.Cutout, "png");
                factory.Storage.Upload(item.ImageThumbKey, Thumbnail, "image/webp");
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
            var entry = NewEntry(day, userId, audience, occasion, exposures);
            db.LeagueEntries.Add(entry);
            await db.SaveChangesAsync(cancellationToken);

            return entry.Id;
        });

    /// <summary>Engine entries in the given buckets (one per element), inserted in one go.</summary>
    public static Task<int> InsertEngineEntriesAsync(
        this ApiFactory factory, DateOnly day, IReadOnlyList<(Audience Audience, Occasion? Occasion)> buckets, CancellationToken cancellationToken) =>
        factory.WithDbAsync(async db =>
        {
            db.LeagueEntries.AddRange(buckets.Select(bucket => NewEntry(day, null, bucket.Audience, bucket.Occasion, 0)));
            return await db.SaveChangesAsync(cancellationToken);
        });

    private static LeagueEntry NewEntry(DateOnly day, Guid? userId, Audience audience, Occasion? occasion, int exposures)
    {
        var entry = new LeagueEntry
        {
            Day = day,
            UserId = userId,
            Kind = userId is null ? LeagueEntryKind.Engine : LeagueEntryKind.Human,
            Audience = audience,
            Occasion = occasion,
            PieceIds = [.. new[] { Guid.NewGuid(), Guid.NewGuid() }.Order()],
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

        return entry;
    }

    /// <summary>A connection of the test's own to the shared database (another transaction beside the API's).</summary>
    public static async Task<NpgsqlConnection> OpenConnectionAsync(this ApiFactory factory, CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(factory.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        return connection;
    }

    public static async Task<int> ExecuteAsync(this NpgsqlConnection connection, string sql, Guid id, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);

        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Waits until another session waits for a row lock in a statement naming <paramref name="table"/> (an API request blocked by the test).</summary>
    public static async Task WaitForLockWaitAsync(this ApiFactory factory, string table, CancellationToken cancellationToken)
    {
        await using var connection = await factory.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM pg_stat_activity WHERE wait_event_type = 'Lock' AND pid <> pg_backend_pid() AND query ILIKE '%' || @table || '%'", connection);
        command.Parameters.AddWithValue("table", table);

        for (var waited = TimeSpan.Zero; waited < TimeSpan.FromSeconds(15); waited += TimeSpan.FromMilliseconds(20))
        {
            if ((long)(await command.ExecuteScalarAsync(cancellationToken))! > 0)
            {
                return;
            }

            await Task.Delay(20, cancellationToken);
        }

        throw new TimeoutException($"No request waited for a lock on {table}.");
    }

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
