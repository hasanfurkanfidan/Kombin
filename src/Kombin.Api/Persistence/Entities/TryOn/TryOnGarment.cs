namespace Kombin.Api.Persistence.Entities.TryOn;

/// <summary>Where a garment row stands with the vision service (<c>docs/design/try-on.md</c>, "The job").</summary>
internal enum TryOnGarmentStatus
{
    /// <summary>No answer yet: asked for by a request, or one transient failure waiting for its one retry.</summary>
    Pending,

    /// <summary>The service answered (<see cref="TryOnGarment.Reliable"/> says whether it can be drawn).</summary>
    Prepared,

    /// <summary>The service refused the photos (a 4xx) or there was no usable photo: not retried until the source changes.</summary>
    Refused,

    /// <summary>The second transient failure in a row: retried after a day.</summary>
    Failed,
}

/// <summary>
/// What <c>/v1/garment/prepare</c> answered for one garment: a wardrobe item (the user's own photo) or a catalogue product (exactly one of the
/// two). One row per owner, updated in place by the <c>tryon-prepare</c> job; the answer fields are null until the first answer and are kept
/// through later failures. A wardrobe item added from the catalogue uses its product's row.
/// </summary>
internal sealed class TryOnGarment : BaseEntity
{
    public const int HashLength = 64;

    public const int MaxErrorLength = 300;

    public Guid? WardrobeItemId { get; set; }

    public Guid? ProductId { get; set; }

    public TryOnGarmentStatus Status { get; set; }

    /// <summary>
    /// SHA-256 (hex) of the source the last attempt was about: the category slug and the cutout key, or the first six image URLs in gallery
    /// order (<c>TryOnSources</c>).
    /// </summary>
    public string SourceHash { get; set; } = string.Empty;

    /// <summary>The source hash of the current answer; differs from <see cref="SourceHash"/> while a new source has not been answered yet.</summary>
    public string? PreparedHash { get; set; }

    /// <summary>The answer's <c>version</c> (<c>prepare-v6</c>); a row of another version than the current one is prepared again.</summary>
    public string? PrepareVersion { get; set; }

    /// <summary><c>top</c>, <c>outerwear</c>, <c>trousers</c>, <c>skirt</c>, <c>shoes</c>, <c>bag</c> or <c>belt</c>.</summary>
    public string? Family { get; set; }

    /// <summary><c>cutout</c> (own alpha), <c>flat</c> (no person) or <c>model</c> (a person in the photo: never reliable).</summary>
    public string? Source { get; set; }

    public bool Reliable { get; set; }

    /// <summary>False only for a top with sleeves folded across the front: such a top is fine under outerwear, never as the outermost layer.</summary>
    public bool? MayBeOutermost { get; set; }

    /// <summary>Tops and outerwear: how each sleeve lies (informational, jsonb as the service sent it).</summary>
    public string? SleevesJson { get; set; }

    public string[] Flags { get; set; } = [];

    /// <summary>Fractions of the cutout, jsonb as the service sent it; sent back to the renderer unchanged.</summary>
    public string? LandmarksJson { get; set; }

    /// <summary>The prepared cutout (RGBA PNG): <c>tryon/{userId}/{garmentId}/prepared.png</c>, private, read by the server only.</summary>
    public string? CutoutKey { get; set; }

    public int? Width { get; set; }

    public int? Height { get; set; }

    /// <summary>Which photo the service used, as a position in the gallery (wardrobe items: 0).</summary>
    public int? ImageIndex { get; set; }

    public DateTimeOffset? PreparedAt { get; set; }

    /// <summary>Transient failures since the last answer (or since the source changed).</summary>
    public int Attempts { get; set; }

    /// <summary>A <c>Pending</c> or <c>Failed</c> row is not tried before this.</summary>
    public DateTimeOffset? NextAttemptAt { get; set; }

    /// <summary>Held by the prepare working on the row; nobody else prepares it before this passes.</summary>
    public DateTimeOffset? LeaseUntil { get; set; }

    /// <summary>A request found the piece without an answer and asked for it: such rows go first, and the request enqueues at most one prepare a minute.</summary>
    public DateTimeOffset? RequestedAt { get; set; }

    /// <summary>The last failure (<c>vision_413: image_too_large</c>, <c>no_images</c>, ...), for whoever looks at a stuck garment.</summary>
    public string? LastError { get; set; }
}
