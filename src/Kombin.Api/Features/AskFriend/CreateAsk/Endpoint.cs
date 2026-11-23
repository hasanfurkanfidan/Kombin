using Kombin.Api.Common.Auth;
using Kombin.Api.Common.RateLimiting;
using Kombin.Api.Common.Results;
using Kombin.Api.Common.Storage;
using Kombin.Api.Common.Time;
using Kombin.Api.Common.Validation;
using Kombin.Api.Features.Wardrobe;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.AskFriend;
using Kombin.Api.Persistence.Entities.Recommendation;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Kombin.Api.Features.AskFriend.CreateAsk;

/// <summary>One outfit (Yes/No) or two (A/B, in this order), and the consent version the asker saw; only the current one is accepted.</summary>
internal sealed record CreateAskRequest(IReadOnlyList<Guid>? OutfitIds, string? ConsentVersion);

/// <summary><c>POST /api/v1/me/asks</c>: "Arkadaşına sor" (design section 5).</summary>
internal static class CreateAskEndpoint
{
    public const int MaxOutfits = 2;

    /// <summary>Pieces of one ask, split evenly between its outfits (16 for one, 8 each for two).</summary>
    public const int MaxPieces = 16;

    public static void Map(RouteGroupBuilder group) =>
        group.MapPost("/", Handle)
            .WithName("CreateAsk")
            .WithValidation<CreateAskRequest>()
            .RequireRateLimiting(RateLimitPolicies.AskFriend)
            .Produces<AskResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

    /// <summary>
    /// Checks, in order: the consent version (409), the age (403), the outfits (the caller's, not dismissed: 404), the daily limit (409), that every
    /// outfit has something to show (409). Then copies the shareable wardrobe thumbnails (a failure deletes the copies and answers 503 with nothing
    /// stored) and writes the consent row (first ask under the version), the ask and its pieces in one save.
    /// </summary>
    private static async Task<IResult> Handle(
        CreateAskRequest request,
        ICurrentUser currentUser,
        KombinDbContext db,
        AskImageCopier copier,
        IObjectStorage storage,
        IClock clock,
        IOptions<AskFriendOptions> options,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var current = AskFriendConsent.Current;
        if (request.ConsentVersion != current.Version)
        {
            return AskFriendErrors.ConsentRequired.ToHttpResult();
        }

        var userId = currentUser.UserId;
        var now = clock.UtcNow;
        var today = Istanbul.Today(now);
        var birthYear = await db.UserProfiles.AsNoTracking()
            .Where(profile => profile.UserId == userId)
            .Select(profile => profile.BirthYear)
            .FirstOrDefaultAsync(cancellationToken);
        if (AgeRule.IsUnderAge(birthYear, today, settings.MinAge))
        {
            return AskFriendErrors.AgeRestricted.ToHttpResult();
        }

        var outfitIds = request.OutfitIds!;
        var outfits = await db.Outfits.AsNoTracking()
            .Include(outfit => outfit.Items)
            .Where(outfit => outfitIds.Contains(outfit.Id) && outfit.UserId == userId && outfit.Status != OutfitStatus.Dismissed)
            .ToDictionaryAsync(outfit => outfit.Id, cancellationToken);
        if (outfits.Count != outfitIds.Count)
        {
            return AskFriendErrors.OutfitNotFound.ToHttpResult();
        }

        var dayStart = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(today.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), Istanbul.Zone), TimeSpan.Zero);
        if (await db.Asks.CountAsync(ask => ask.UserId == userId && ask.CreatedAt >= dayStart, cancellationToken) >= settings.MaxAsksPerDay)
        {
            return AskFriendErrors.DailyLimit.ToHttpResult();
        }

        var planned = await PlanAsync(db, userId, [.. outfitIds.Select(id => outfits[id])], cancellationToken);
        if (planned.GroupBy(piece => piece.Option).Count() != outfitIds.Count
            || planned.GroupBy(piece => piece.Option).Any(option => option.All(piece => piece.ThumbKey is null && piece.ImageUrl is null)))
        {
            return AskFriendErrors.NothingToShow.ToHttpResult();
        }

        var sources = planned.Where(piece => piece.ThumbKey is not null).Select(piece => piece.ThumbKey!).ToList();
        if (await copier.CopyAllAsync(sources, cancellationToken) is not { } copies)
        {
            return AskFriendErrors.StorageUnavailable.ToHttpResult();
        }

        var ask = new Ask
        {
            UserId = userId,
            Token = AskToken.New(),
            Kind = outfitIds.Count == 1 ? AskKind.Single : AskKind.Pair,
            Status = AskStatus.Open,
            ConsentVersion = current.Version,
            CreatedAt = now,
            ExpiresAt = now + settings.AnswerWindow,
        };

        var copyIndex = 0;
        foreach (var piece in planned)
        {
            ask.Pieces.Add(new AskPiece
            {
                AskId = ask.Id,
                Option = piece.Option,
                OutfitId = piece.OutfitId,
                Slot = piece.Slot,
                Position = piece.Position,
                WardrobeItemId = piece.WardrobeItemId,
                ProductId = piece.ProductId,
                CopyKey = piece.ThumbKey is null ? null : copies[copyIndex++],
                ImageUrl = piece.ThumbKey is null ? piece.ImageUrl : null,
            });
        }

        db.Asks.Add(ask);
        var consentAdded = !await db.AskConsents.AnyAsync(consent => consent.UserId == userId && consent.Version == current.Version, cancellationToken);
        var consent = new AskConsentRecord { UserId = userId, Version = current.Version, Text = current.Text, AcceptedAt = now };
        if (consentAdded)
        {
            db.AskConsents.Add(consent);
        }

        try
        {
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException exception) when (consentAdded && exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                // A double tap on the first ask: the other request wrote this version's consent row first; keep that one.
                db.Entry(consent).State = EntityState.Detached;
                await db.SaveChangesAsync(cancellationToken);
            }
        }
        catch
        {
            await copier.DeleteAsync(copies, CancellationToken.None);
            throw;
        }

        var response = await AskResponses.BuildAsync(db, storage, settings, ask, now, cancellationToken);
        return TypedResults.Created($"/api/v1/me/asks/{ask.Id}", response);
    }

    /// <summary>
    /// The shown slots, outfit by outfit (A, then B) and in slot order inside one: a shareable wardrobe piece with a thumbnail gets a copy, a catalog
    /// piece its product's image, anything else a grey tile.
    /// </summary>
    private static async Task<List<PlannedPiece>> PlanAsync(KombinDbContext db, Guid userId, IReadOnlyList<Outfit> outfits, CancellationToken cancellationToken)
    {
        var perOutfit = MaxPieces / outfits.Count;
        var wardrobeIds = outfits.SelectMany(outfit => outfit.Items).Where(item => item.WardrobeItemId is not null).Select(item => item.WardrobeItemId!.Value).Distinct().ToList();
        var productIds = outfits.SelectMany(outfit => outfit.Items).Where(item => item.ProductId is not null).Select(item => item.ProductId!.Value).Distinct().ToList();

        var wardrobe = await db.WardrobeItems.AsNoTracking()
            .Where(item => wardrobeIds.Contains(item.Id) && item.UserId == userId)
            .ToDictionaryAsync(item => item.Id, cancellationToken);
        var productImages = await db.Products.AsNoTracking()
            .Where(product => productIds.Contains(product.Id) && product.PrimaryImageUrl != null)
            .Select(product => new { product.Id, product.PrimaryImageUrl })
            .ToDictionaryAsync(product => product.Id, product => product.PrimaryImageUrl!, cancellationToken);

        var planned = new List<PlannedPiece>();
        for (var index = 0; index < outfits.Count; index++)
        {
            var outfit = outfits[index];
            var option = index == 0 ? AskOption.A : AskOption.B;
            var items = outfit.Items.OrderBy(item => (int)item.Slot).ThenBy(item => item.Position).ThenBy(item => item.Id).Take(perOutfit).ToList();

            for (var position = 0; position < items.Count; position++)
            {
                var item = items[position];
                var wardrobeItem = item.WardrobeItemId is { } wardrobeItemId ? wardrobe.GetValueOrDefault(wardrobeItemId) : null;
                var thumbKey = wardrobeItem is { ImageThumbKey: { } thumb } && WardrobeModerator.IsShareable(wardrobeItem) ? thumb : null;
                var imageUrl = thumbKey is null && item.ProductId is { } productId && productImages.TryGetValue(productId, out var url) && url.Length <= AskPiece.MaxImageUrlLength
                    ? url
                    : null;

                planned.Add(new PlannedPiece(option, outfit.Id, item.Slot, (short)position, wardrobeItem?.Id, item.ProductId, thumbKey, imageUrl));
            }
        }

        return planned;
    }

    private sealed record PlannedPiece(
        AskOption Option, Guid OutfitId, OutfitSlot Slot, short Position, Guid? WardrobeItemId, Guid? ProductId, string? ThumbKey, string? ImageUrl);
}
