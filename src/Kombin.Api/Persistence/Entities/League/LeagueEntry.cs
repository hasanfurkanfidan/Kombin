using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Recommendation;

namespace Kombin.Api.Persistence.Entities.League;

/// <summary>
/// One outfit in one day's pool: a player's worn outfit (one per player per day) or an engine outfit (no user). Its ID is shown to its owner only;
/// voters see a pair ID and the pieces' images.
/// </summary>
internal sealed class LeagueEntry : BaseEntity
{
    public const int PieceSetHashLength = 32;

    public DateOnly Day { get; set; }

    /// <summary>Null for an engine outfit.</summary>
    public Guid? UserId { get; set; }

    public LeagueEntryKind Kind { get; set; }

    /// <summary>The worn <c>recommendation.outfits</c> row it came from; null for an engine outfit or once that row is gone.</summary>
    public Guid? OutfitId { get; set; }

    public Audience Audience { get; set; }

    /// <summary>Null is a bucket of its own for pairing.</summary>
    public Occasion? Occasion { get; set; }

    /// <summary>Sorted IDs of the non-accessory pieces (wardrobe items; products for an engine outfit): the repeat rule compares these.</summary>
    public Guid[] PieceIds { get; set; } = [];

    /// <summary>SHA-256 hex (32) of <see cref="PieceIds"/>, like <c>OutfitHash</c>: the 30-day exact repeat check.</summary>
    public string PieceSetHash { get; set; } = string.Empty;

    public LeagueEntryStatus Status { get; set; }

    /// <summary>Only for <see cref="LeagueEntryStatus.NotCompeting"/>.</summary>
    public LeagueEntryReason? Reason { get; set; }

    /// <summary>Pairs served with this entry; pairing favours the fewest.</summary>
    public int Exposures { get; set; }

    /// <summary>Counted votes on this entry (unweighted): ranked from <c>League:MinComparisons</c>.</summary>
    public int Comparisons { get; set; }

    /// <summary>Weighted wins of counted votes: the win rate shown after a vote.</summary>
    public double WeightedWins { get; set; }

    /// <summary>Weighted games of counted votes.</summary>
    public double WeightedGames { get; set; }

    /// <summary>Reports from trusted reporters since the last review; at <c>League:Reports:HideAt</c> the entry leaves the pool.</summary>
    public int CountingReports { get; set; }

    /// <summary>The first time it was served; after it the entry no longer follows the player's later wears that day.</summary>
    public DateTimeOffset? LockedAt { get; set; }

    /// <summary>Bradley–Terry strength, written by the day close.</summary>
    public double? Strength { get; set; }

    /// <summary>0-1, 1 the best; ranked human entries only.</summary>
    public double? Percentile { get; set; }

    /// <summary>1 = best, among ranked human entries.</summary>
    public int? Rank { get; set; }

    public DateTimeOffset? ReviewedAt { get; set; }

    public List<LeagueEntryPiece> Pieces { get; } = [];
}

/// <summary>One piece of an entry as voters see it: accessories included (they are only left out of <see cref="LeagueEntry.PieceIds"/>).</summary>
internal sealed class LeagueEntryPiece
{
    public const int MaxImageKeyLength = 300;

    public const int MaxImageUrlLength = 2048;

    public Guid EntryId { get; set; }

    public short Position { get; set; }

    public OutfitSlot Slot { get; set; }

    public Guid? WardrobeItemId { get; set; }

    public Guid? ProductId { get; set; }

    /// <summary>The league's own copy of the thumbnail under random key segments (never the owner's ID); public by its <c>thumb.*</c> name.</summary>
    public string? ImageKey { get; set; }

    /// <summary>A catalog image URL, for engine pieces and catalog items without a stored thumbnail.</summary>
    public string? ImageUrl { get; set; }
}
