using Kombin.Api.Common.Storage;
using Kombin.Api.Common.Time;
using Kombin.Api.Features.League.Rules;
using Kombin.Api.Features.Wardrobe;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.League;
using Kombin.Api.Persistence.Entities.Recommendation;
using Kombin.Api.Persistence.Entities.Wardrobe;
using Microsoft.Extensions.Options;

namespace Kombin.Api.Features.League;

/// <summary>What one resolution did: the player's entry of the day afterwards (null: none), and how many safety checks it asked for.</summary>
internal sealed record EntryResolution(LeagueEntry? Entry, int ModerationCalls);

/// <summary>
/// Turns a player's worn outfit of the day into their league entry (design note sections 4 and 5). Called by the <c>league-entries</c> job (which
/// may ask the safety filter for pieces without a verdict) and by <c>GET /league/me</c> (which never waits for it).
/// <list type="bullet">
/// <item>Only for a joined player under the current consent, in good standing (not disabled, no deletion pending), before the day's close.</item>
/// <item>The entry follows the player's latest worn outfit until it is first served (<c>locked_at</c>); withdrawn and removed entries are final.</item>
/// <item>Eligibility is the pure <see cref="EntryEligibility"/>; the image safety filter is Wardrobe's <see cref="WardrobeModerator"/>, the
/// one seam this class uses.</item>
/// <item>Images: when the entry becomes <c>Competing</c>, each piece's thumbnail is copied to <c>league/{random}/{random}/thumb.*</c>, public by
/// its name and linked to no one (wardrobe keys carry the owner's ID). A catalog item without a stored thumbnail shows its product's image.
/// Replaced copies are deleted after the commit; withdrawn and removed entries' copies by <see cref="ReleaseCopiesAsync"/>.</item>
/// </list>
/// One player's resolutions are serialised by a transaction-scoped advisory lock, and the entry row is read <c>FOR UPDATE</c>, so a pair being
/// served at the same moment either sees the old entry (and locks it) or the new one.
/// </summary>
internal sealed partial class EntryMaterializer(
    KombinDbContext db,
    IObjectStorage storage,
    WardrobeModerator moderator,
    IOptions<LeagueOptions> options,
    ILogger<EntryMaterializer> logger)
{
    /// <summary>Copies released per batch by <see cref="ReleaseCopiesAsync"/>.</summary>
    public const int ReleaseBatch = 500;

    public async Task<EntryResolution> ResolveAsync(Guid userId, DateTimeOffset now, int moderationBudget, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var day = Istanbul.Today(now);
        if (now >= LeagueDays.ClosesAt(day, settings.DayClosesAt) || !await InGoodStandingAsync(userId, cancellationToken))
        {
            return new EntryResolution(null, 0);
        }

        // Already fixed for the day (withdrawn, removed, or in front of voters): nothing to resolve, and no safety check to spend.
        var settled = await db.LeagueEntries.AsNoTracking().SingleOrDefaultAsync(entry => entry.Day == day && entry.UserId == userId, cancellationToken);
        if (settled is { Status: LeagueEntryStatus.Withdrawn or LeagueEntryStatus.Removed } || settled?.LockedAt is not null)
        {
            return new EntryResolution(settled, 0);
        }

        var outfit = await TodaysOutfit.ResolveAsync(db, userId, day, settings.DayClosesAt, cancellationToken);
        if (outfit is null)
        {
            return new EntryResolution(settled, 0);
        }

        var outfitItems = outfit.Items.OrderBy(item => item.Position).ThenBy(item => item.Id).ToList();
        var itemIds = outfitItems.Select(item => item.WardrobeItemId).OfType<Guid>().Distinct().ToList();
        var items = await db.WardrobeItems
            .Where(item => itemIds.Contains(item.Id) && item.UserId == userId)
            .ToDictionaryAsync(item => item.Id, cancellationToken);

        // The safety filter, for pieces it has not answered yet: the job asks (up to its budget per run); GET /league/me does not wait for it.
        var moderationCalls = 0;
        foreach (var item in items.Values.Where(item => item.ModerationVerdict is null && !item.IsArchived && item.Status == WardrobeItemStatus.Ready).OrderBy(item => item.Id))
        {
            if (moderationCalls >= moderationBudget)
            {
                break;
            }

            moderationCalls++;
            await moderator.ModerateAsync(item, null, string.Empty, cancellationToken);
        }

        var productImages = await ProductImagesAsync([.. items.Values.Select(item => item.ProductId).OfType<Guid>()], cancellationToken);
        var pieces = outfitItems.Select(piece => Describe(piece, items, productImages)).ToList();
        var pieceIds = RepeatRule.PieceIds(outfitItems.Where(piece => piece.WardrobeItemId is not null).Select(piece => (piece.Slot, piece.WardrobeItemId!.Value)));
        var profile = await db.UserProfiles.AsNoTracking()
            .Where(row => row.UserId == userId)
            .Select(row => new { row.BirthYear, row.Audience })
            .FirstOrDefaultAsync(cancellationToken);

        var verdict = EntryEligibility.Evaluate(
            LeagueRules.IsUnderAge(profile?.BirthYear, day, settings.MinAge),
            [.. pieces.Select(piece => piece.Eligibility)],
            settings.MinPieces,
            RepeatRule.IsBlocked(pieceIds, day, await EarlierEntriesAsync(userId, day, cancellationToken), settings));

        var resolved = await WriteAsync(userId, day, outfit, profile?.Audience, pieces, pieceIds, verdict, cancellationToken);

        return new EntryResolution(resolved, moderationCalls);
    }

    /// <summary>
    /// Deletes the image copies of withdrawn and removed entries (any day) and clears their keys: the "within minutes" of leaving or withdrawing.
    /// <paramref name="userIds"/> scopes it for tests. Returns the number of copies deleted.
    /// </summary>
    public async Task<int> ReleaseCopiesAsync(IReadOnlyCollection<Guid>? userIds, CancellationToken cancellationToken)
    {
        var count = 0;

        while (true)
        {
            var released = db.LeagueEntries.Where(entry => entry.Status == LeagueEntryStatus.Withdrawn || entry.Status == LeagueEntryStatus.Removed);
            if (userIds is not null)
            {
                released = released.Where(entry => entry.UserId != null && userIds.Contains(entry.UserId.Value));
            }

            var query = db.LeagueEntryPieces.Where(piece => piece.ImageKey != null && released.Any(entry => entry.Id == piece.EntryId));

            var batch = await query.OrderBy(piece => piece.EntryId).ThenBy(piece => piece.Position)
                .Select(piece => new { piece.EntryId, piece.Position, Key = piece.ImageKey! })
                .Take(ReleaseBatch)
                .ToListAsync(cancellationToken);

            if (batch.Count == 0)
            {
                return count;
            }

            await storage.DeleteAsync([.. batch.Select(piece => piece.Key)], cancellationToken);

            var keys = batch.Select(piece => piece.Key).ToList();
            await db.LeagueEntryPieces.Where(piece => piece.ImageKey != null && keys.Contains(piece.ImageKey))
                .ExecuteUpdateAsync(setters => setters.SetProperty(piece => piece.ImageKey, (string?)null), cancellationToken);

            count += batch.Count;
        }
    }

    private async Task<bool> InGoodStandingAsync(Guid userId, CancellationToken cancellationToken)
    {
        var current = LeagueConsent.Current.Version;

        return await db.LeaguePlayers.AsNoTracking().AnyAsync(
            player => player.UserId == userId && player.LeftAt == null && player.ConsentVersion == current
                && db.Users.Any(user => user.Id == userId && user.DisabledAt == null && user.DeletionRequestedAt == null),
            cancellationToken);
    }

    /// <summary>The player's entries within the longest repeat window before <paramref name="day"/>, and whether each competed at its close.</summary>
    private async Task<List<PastEntry>> EarlierEntriesAsync(Guid userId, DateOnly day, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var from = day.AddDays(-(Math.Max(settings.RepeatExactDays, settings.RepeatNearDays) - 1));
        var until = day.AddDays(-1);

        var rows = await db.LeagueEntries.AsNoTracking()
            .Where(entry => entry.UserId == userId && entry.Day >= from && entry.Day <= until)
            .Select(entry => new
            {
                entry.Day,
                entry.PieceIds,
                Competed = db.LeagueDailyScores.Any(score => score.UserId == userId && score.Day == entry.Day && score.EntryId == entry.Id && score.Competed),
            })
            .ToListAsync(cancellationToken);

        return [.. rows.Select(row => new PastEntry(row.Day, row.PieceIds, row.Competed))];
    }

    private async Task<Dictionary<Guid, string>> ProductImagesAsync(List<Guid> productIds, CancellationToken cancellationToken) =>
        productIds.Count == 0
            ? []
            : await db.Products.AsNoTracking()
                .Where(product => productIds.Contains(product.Id) && product.PrimaryImageUrl != null)
                .ToDictionaryAsync(product => product.Id, product => product.PrimaryImageUrl!, cancellationToken);

    private static PieceInput Describe(OutfitItem piece, Dictionary<Guid, WardrobeItem> items, Dictionary<Guid, string> productImages)
    {
        WardrobeItem? item = piece.WardrobeItemId is { } id && items.TryGetValue(id, out var found) ? found : null;
        var thumbKey = item?.ImageThumbKey ?? item?.ImageCutoutKey;
        var productImage = item?.ProductId is { } productId && productImages.TryGetValue(productId, out var url) ? url : null;

        return new PieceInput(
            piece.Slot,
            item,
            thumbKey,
            productImage,
            new EligibilityPiece(piece.Slot, piece.WardrobeItemId, item is not null, item?.IsArchived ?? false, item?.ModerationVerdict, thumbKey is not null || productImage is not null));
    }

    private async Task<LeagueEntry?> WriteAsync(
        Guid userId,
        DateOnly day,
        Outfit outfit,
        Audience? audience,
        List<PieceInput> pieces,
        Guid[] pieceIds,
        EntryVerdict verdict,
        CancellationToken cancellationToken)
    {
        var copies = new List<string>();
        var released = new List<string>();

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({$"league-entry:{userId:N}:{day.DayNumber}"}, 0))", cancellationToken);

        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM league.entries WHERE day = {day} AND user_id = {userId} FOR UPDATE", cancellationToken);
        var entry = await db.LeagueEntries.Include(row => row.Pieces).SingleOrDefaultAsync(row => row.Day == day && row.UserId == userId, cancellationToken);

        // Final for the day (withdrawn, removed), or already in front of voters: the entry no longer follows the player's wears.
        if (entry is { Status: LeagueEntryStatus.Withdrawn or LeagueEntryStatus.Removed } || entry?.LockedAt is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return entry;
        }

        // Already competing with this outfit: nothing to redo (no second copy of the images).
        if (entry is { Status: LeagueEntryStatus.Competing } && entry.OutfitId == outfit.Id)
        {
            await transaction.CommitAsync(cancellationToken);
            return entry;
        }

        try
        {
            var newPieces = new List<LeagueEntryPiece>();
            if (verdict.Status == LeagueEntryStatus.Competing)
            {
                foreach (var (piece, position) in pieces.Select((piece, position) => (piece, position)))
                {
                    var copy = piece.ThumbKey is { } key ? await CopyAsync(key, cancellationToken) : null;
                    if (copy is null && piece.ProductImage is null)
                    {
                        // The stored thumbnail is gone and there is nothing else to show: the outfit cannot be shown to voters.
                        verdict = EntryVerdict.Not(LeagueEntryReason.PieceUnavailable);
                        break;
                    }

                    if (copy is not null)
                    {
                        copies.Add(copy);
                    }

                    newPieces.Add(Piece(piece, position, copy, copy is null ? piece.ProductImage : null));
                }
            }

            if (verdict.Status != LeagueEntryStatus.Competing)
            {
                await DeleteQuietlyAsync(copies, cancellationToken);
                copies.Clear();
                newPieces = [.. pieces.Select((piece, position) => Piece(piece, position, null, null))];
            }

            if (entry is null)
            {
                entry = new LeagueEntry { Day = day, UserId = userId, Kind = LeagueEntryKind.Human };
                db.LeagueEntries.Add(entry);
            }
            else
            {
                released.AddRange(entry.Pieces.Select(piece => piece.ImageKey).OfType<string>());
                db.LeagueEntryPieces.RemoveRange(entry.Pieces);
                entry.Pieces.Clear();
                await db.SaveChangesAsync(cancellationToken);
            }

            entry.OutfitId = outfit.Id;
            entry.Audience = audience ?? Audience.Unisex;
            entry.Occasion = outfit.Occasion;
            entry.PieceIds = pieceIds;
            entry.PieceSetHash = RepeatRule.Hash(pieceIds);
            entry.Status = verdict.Status;
            entry.Reason = verdict.Reason;
            entry.Pieces.AddRange(newPieces);

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await DeleteQuietlyAsync(copies, CancellationToken.None);
            throw;
        }

        await DeleteQuietlyAsync(released, cancellationToken);

        return entry;
    }

    private static LeagueEntryPiece Piece(PieceInput piece, int position, string? imageKey, string? imageUrl) => new()
    {
        Position = (short)position,
        Slot = piece.Slot,
        WardrobeItemId = piece.Item?.Id,
        ProductId = piece.Item?.ProductId,
        ImageKey = imageKey,
        ImageUrl = imageUrl,
    };

    /// <summary>The league's own copy of a thumbnail under random key segments; null when the source object is gone.</summary>
    private async Task<string?> CopyAsync(string sourceKey, CancellationToken cancellationToken)
    {
        if (await storage.GetAsync(sourceKey, cancellationToken) is not { } stored)
        {
            return null;
        }

        var extension = StorageKeys.ExtensionFor(stored.ContentType) ?? Path.GetExtension(sourceKey).TrimStart('.');
        var key = StorageKeys.Build("league", Guid.NewGuid(), Guid.NewGuid(), ImageVariant.Thumb, string.IsNullOrEmpty(extension) ? "webp" : extension);
        await storage.PutAsync(key, stored.Content, stored.ContentType, cancellationToken);

        return key;
    }

    private async Task DeleteQuietlyAsync(List<string> keys, CancellationToken cancellationToken)
    {
        if (keys.Count == 0)
        {
            return;
        }

        try
        {
            await storage.DeleteAsync(keys, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogCopiesLeft(logger, keys.Count, exception);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "League: {Count} image copies could not be deleted and are left in storage.")]
    private static partial void LogCopiesLeft(ILogger logger, int count, Exception exception);

    private sealed record PieceInput(OutfitSlot Slot, WardrobeItem? Item, string? ThumbKey, string? ProductImage, EligibilityPiece Eligibility);
}
