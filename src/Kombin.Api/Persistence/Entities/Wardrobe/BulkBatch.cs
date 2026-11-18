namespace Kombin.Api.Persistence.Entities.Wardrobe;

internal enum BulkBatchStatus
{
    /// <summary>The presigned upload URL was handed out; the photo may not be in storage yet.</summary>
    Created,

    /// <summary><c>process</c> enqueued <c>bulk-add-analyze</c>; the app polls.</summary>
    Processing,

    /// <summary>The pieces are staged for the review screen (possibly none, with <see cref="BulkBatch.UnseparatedCount"/> above 0).</summary>
    Ready,

    /// <summary>Vision refused the photo (<see cref="BulkBatch.Refusal"/>); the photo is already deleted.</summary>
    Refused,

    /// <summary>The analysis did not happen (<see cref="BulkBatch.FailureCode"/>); the photo is kept so "tekrar dene" needs no second upload.</summary>
    Failed,

    Confirmed,

    Cancelled,

    /// <summary>Never confirmed within <c>BulkAdd:StagingRetention</c>; staging and pieces are gone.</summary>
    Expired,
}

internal enum BulkFailureCode
{
    /// <summary>The vision service answered busy twice.</summary>
    VisionBusy,

    /// <summary>The vision service was unreachable, timed out or failed on its side.</summary>
    VisionUnavailable,

    /// <summary>The service refused the file itself (a 4xx): sending it again would not help.</summary>
    PhotoUnreadable,

    /// <summary>The answer was not usable (not base64, an embedding of the wrong size).</summary>
    AnalysisFailed,

    /// <summary>The job died while the batch was <c>Processing</c>; the cleanup marked it.</summary>
    Interrupted,
}

/// <summary>
/// One photo of several garments laid out ("Toplu ekle", <c>docs/design/bulk-add.md</c>): the staging area between the upload and the review
/// screen. Nothing else reads these tables, so an unconfirmed piece can never leak into the wardrobe, the league or the outfit engine. After the
/// end the row stays as a tombstone for <c>BulkAdd:TombstoneRetention</c> and holds counts and enums only: no image key and no piece. Cascades on the user.
/// </summary>
internal sealed class BulkBatch : BaseEntity
{
    public const int MaxKeyLength = 300;

    public Guid UserId { get; set; }

    public BulkBatchStatus Status { get; set; }

    /// <summary>The room photo, private under <c>bulk/{user}/{batch}/</c>; null once it was deleted (vision answered, or the batch ended).</summary>
    public string? OriginalKey { get; set; }

    /// <summary>When <c>process</c> last enqueued the analysis; another call soon after does not enqueue it again.</summary>
    public DateTimeOffset? ProcessRequestedAt { get; set; }

    /// <summary>Whether the batch counts toward the daily limit: a started photo that vision answered (a refusal included), never a failed one.</summary>
    public bool Counted { get; set; }

    /// <summary>The service's refusal code (<c>closet</c>, <c>rail</c>, ...) of a <c>Refused</c> batch; kept as sent, the sentence is <c>BulkAddCopy</c>'s.</summary>
    public string? Refusal { get; set; }

    public BulkFailureCode? FailureCode { get; set; }

    /// <summary>What the service or the job said, for whoever looks at a failed batch; never shown to the user.</summary>
    public string? FailureDetail { get; set; }

    /// <summary>Failed calls to the vision service for the current analysis.</summary>
    public int AnalysisAttempts { get; set; }

    /// <summary>Garments the service saw together and did not cut apart.</summary>
    public int UnseparatedCount { get; set; }

    /// <summary>Their regions, as the service sent them (jsonb: a list of boxes in fractions of the photo).</summary>
    public string? UnseparatedRegions { get; set; }

    /// <summary>Pieces the service found beyond <c>BulkAdd:MaxPiecesPerPhoto</c> and the batch left out; the status answer says so.</summary>
    public int CutOffCount { get; set; }

    /// <summary>The service's own total time for the photo (<c>stagesMs.total</c>), for the operators.</summary>
    public int? VisionMs { get; set; }

    /// <summary>The vision model the pieces' embeddings came from, like <c>WardrobeItem.EmbeddingModel</c>.</summary>
    public string? EmbeddingModel { get; set; }

    /// <summary>Created plus <c>BulkAdd:StagingRetention</c>.</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    // The batch's own counters, the source of truth for the brief's stop rule (server side, immune to dropped events).
    public int PiecesShown { get; set; }

    public int PiecesPreTicked { get; set; }

    public int PiecesTicked { get; set; }

    public int TickedPreTicked { get; set; }

    public int CategoryEdits { get; set; }

    public int ColourEdits { get; set; }

    public DateTimeOffset? ConfirmedAt { get; set; }

    /// <summary>The wardrobe items a confirm created, in reading order (a piece's ID is its item's ID); a repeated confirm answers these.</summary>
    public Guid[] ConfirmedItemIds { get; set; } = [];

    /// <summary>When confirm, cancel or the end of the batch last made sure no object of it is left; null while staged files may exist.</summary>
    public DateTimeOffset? ObjectsDeletedAt { get; set; }

    /// <summary>When the batch became <c>Confirmed</c>, <c>Cancelled</c> or <c>Expired</c>; the tombstone's age counts from it.</summary>
    public DateTimeOffset? EndedAt { get; set; }
}
