namespace Kombin.Api.Features.Wardrobe;

/// <summary>
/// The Turkish copy of the wardrobe summary. It follows the app glossary (<c>CopyGlossaryTests</c> scans this file): a piece is a "parça", one
/// with no companion is "eşi yok", and an estimate is always "tahmini". A number is never alone: the messages that carry one say what it counts.
/// </summary>
internal static class WardrobeSummaryCopy
{
    /// <summary>What the app writes next to a price or a total that is not the user's own.</summary>
    public const string Estimated = "tahmini";

    public const string NoPartnerReason = "eşi yok";

    public const string OutOfSeasonReason = "mevsimi değil";

    /// <summary>The group of pieces that have no category yet.</summary>
    public const string UncategorisedName = "Kategorisiz";

    public const string EmptyWardrobe = "Dolabın henüz boş. Birkaç parça ekleyince dolap değerini ve giymediklerini burada görürsün.";

    public const string NothingPriced = "Fiyatını bildiğin parçalar olunca dolap değerin burada görünür.";

    public const string NothingUnworn = "Giymediğin ya da kombinlerine koymadığın parça yok.";

    /// <summary>The wardrobe has some pieces, not enough for a summary that means anything.</summary>
    public static string FewPieces(int pieceCount, int minimum) =>
        $"Dolabında {pieceCount} parça var. En az {minimum} parça olunca dolap değerini ve giymediklerini gösterebiliriz.";
}
