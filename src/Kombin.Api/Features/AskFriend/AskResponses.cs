using Kombin.Api.Common.Storage;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.AskFriend;
using Kombin.Api.Persistence.Entities.Recommendation;

namespace Kombin.Api.Features.AskFriend;

/// <summary>One shown slot, as the asker sees it: their own wardrobe thumbnail or the catalog image; null for a grey tile or a piece that is gone.</summary>
internal sealed record AskPieceResponse(OutfitSlot Slot, string? ImageUrl);

/// <summary>One outfit of the ask (<c>A</c>, and <c>B</c> for a pair).</summary>
internal sealed record AskOptionResponse(AskOption Option, Guid? OutfitId, IReadOnlyList<AskPieceResponse> Pieces);

/// <summary>The answers per choice; hidden answers are not counted.</summary>
internal sealed record AskTally(int Yes, int No, int A, int B, int Total);

internal sealed record AskAnswerResponse(Guid Id, AskChoice Choice, string? Name, string? Note, DateTimeOffset AnsweredAt);

/// <summary>
/// One ask for its asker. <c>Url</c> is the share link, only while <c>Open</c>; <c>StatusMessage</c> is set only for <c>Hidden</c>. Answers are newest
/// first and leave out the hidden ones.
/// </summary>
internal sealed record AskResponse(
    Guid Id,
    AskKind Kind,
    AskViewState Status,
    string? StatusMessage,
    string? Url,
    string ShareText,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<AskOptionResponse> Options,
    AskTally Tally,
    IReadOnlyList<AskAnswerResponse> Answers);

/// <summary>An ask in the asker's list. <c>Leading</c> is null on a tie or without answers.</summary>
internal sealed record AskSummary(Guid Id, AskKind Kind, AskViewState Status, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, int Total, AskChoice? Leading);

internal static class AskResponses
{
    /// <summary>The asker's view of <paramref name="ask"/>, whose <see cref="Ask.Pieces"/> must be loaded.</summary>
    public static async Task<AskResponse> BuildAsync(
        KombinDbContext db, IObjectStorage storage, AskFriendOptions options, Ask ask, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ask);

        var wardrobeIds = ask.Pieces.Where(piece => piece.WardrobeItemId is not null).Select(piece => piece.WardrobeItemId!.Value).Distinct().ToList();
        var items = await db.WardrobeItems.AsNoTracking()
            .Where(item => wardrobeIds.Contains(item.Id) && item.UserId == ask.UserId)
            .Select(item => new { item.Id, item.ImageThumbKey, item.ProductId, item.FromCatalog })
            .ToDictionaryAsync(item => item.Id, cancellationToken);

        var productIds = ask.Pieces.Where(piece => piece.ProductId is not null).Select(piece => piece.ProductId!.Value)
            .Concat(items.Values.Where(item => item.FromCatalog && item.ProductId is not null).Select(item => item.ProductId!.Value))
            .Distinct()
            .ToList();
        var productImages = await db.Products.AsNoTracking()
            .Where(product => productIds.Contains(product.Id))
            .Select(product => new { product.Id, product.PrimaryImageUrl })
            .ToDictionaryAsync(product => product.Id, product => product.PrimaryImageUrl, cancellationToken);

        string? ImageOf(AskPiece piece)
        {
            if (piece.WardrobeItemId is { } wardrobeItemId)
            {
                if (!items.TryGetValue(wardrobeItemId, out var item))
                {
                    return null;
                }

                if (item.ImageThumbKey is { } thumb)
                {
                    return storage.GetPublicUrl(thumb);
                }

                return item.FromCatalog && item.ProductId is { } catalogId ? productImages.GetValueOrDefault(catalogId) : null;
            }

            return piece.ProductId is { } productId && productImages.TryGetValue(productId, out var primary) ? piece.ImageUrl ?? primary : null;
        }

        var optionResponses = ask.Pieces
            .GroupBy(piece => piece.Option)
            .OrderBy(group => group.Key)
            .Select(group =>
            {
                var pieces = group.OrderBy(piece => piece.Position).ToList();
                return new AskOptionResponse(
                    group.Key,
                    pieces.Select(piece => piece.OutfitId).FirstOrDefault(outfitId => outfitId is not null),
                    [.. pieces.Select(piece => new AskPieceResponse(piece.Slot, ImageOf(piece)))]);
            })
            .ToList();

        var answers = await db.AskAnswers.AsNoTracking()
            .Where(answer => answer.AskId == ask.Id && answer.HiddenAt == null)
            .OrderByDescending(answer => answer.UpdatedAt)
            .ThenByDescending(answer => answer.Id)
            .Select(answer => new AskAnswerResponse(answer.Id, answer.Choice, answer.Name, answer.Note, answer.UpdatedAt))
            .ToListAsync(cancellationToken);

        var state = AskState.Of(ask, now);
        var counts = answers.GroupBy(answer => answer.Choice).ToDictionary(group => group.Key, group => group.Count());

        return new AskResponse(
            ask.Id,
            ask.Kind,
            state,
            state == AskViewState.Hidden ? AskFriendCopy.HiddenStatusMessage : null,
            state == AskViewState.Open ? LinkOf(options, ask.Token) : null,
            AskFriendCopy.ShareText(ask.Kind),
            ask.CreatedAt,
            ask.ExpiresAt,
            optionResponses,
            Tally(counts),
            answers);
    }

    /// <summary>The public page of an ask: <c>{PublicBaseUrl}/sor/{token}</c>.</summary>
    public static string LinkOf(AskFriendOptions options, string token)
    {
        ArgumentNullException.ThrowIfNull(options);

        return (options.PublicBaseUrl ?? string.Empty).TrimEnd('/') + "/sor/" + token;
    }

    public static AskTally Tally(IReadOnlyDictionary<AskChoice, int> counts)
    {
        ArgumentNullException.ThrowIfNull(counts);

        int Count(AskChoice choice) => counts.GetValueOrDefault(choice);

        return new AskTally(Count(AskChoice.Yes), Count(AskChoice.No), Count(AskChoice.A), Count(AskChoice.B), counts.Values.Sum());
    }

    /// <summary>The choice with the most answers; null without answers or when two choices share the top count.</summary>
    public static AskChoice? Leading(IReadOnlyDictionary<AskChoice, int> counts)
    {
        ArgumentNullException.ThrowIfNull(counts);

        var ranked = counts.Where(entry => entry.Value > 0).OrderByDescending(entry => entry.Value).ToList();
        if (ranked.Count == 0 || (ranked.Count > 1 && ranked[0].Value == ranked[1].Value))
        {
            return null;
        }

        return ranked[0].Key;
    }
}
