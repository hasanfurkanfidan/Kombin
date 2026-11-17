namespace Kombin.Api.Persistence.Entities.TryOn;

/// <summary>
/// A stored mannequin drawing of one outfit for one user, or the renderer's refusal of exactly these inputs. It reveals the user's body proportions: private object, presigned GETs, deleted when
/// the measurements change, after <c>TryOn:RenderRetention</c> unused and with the account.
/// </summary>
internal sealed class TryOnRender
{
    public const int HashLength = 64;

    public const int MaxRendererLength = 40;

    public Guid Id { get; init; } = Guid.CreateVersion7();

    public Guid UserId { get; set; }

    /// <summary>SHA-256 (hex) of the renderer, the body, every garment (row, version, prepared at, size, reference size, cut) and the width.</summary>
    public string CacheKey { get; set; } = string.Empty;

    /// <summary>SHA-256 (hex) of the body values alone: a render whose body is not the user's current one is deleted.</summary>
    public string BodyHash { get; set; } = string.Empty;

    public string Renderer { get; set; } = string.Empty;

    /// <summary><c>tryon/{userId}/{renderId}/render.webp</c>; null for a refusal.</summary>
    public string? ImageKey { get; set; }

    /// <summary>
    /// The renderer's <c>422</c> code (<c>incomplete_outfit</c>, <c>folded_sleeves</c>, ...) when it refused these very inputs: the answer is then
    /// <c>Unavailable</c> without asking again (never a retry); any change of the inputs is another key and another try.
    /// </summary>
    public string? Refusal { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    /// <summary>The response's garments, <c>[{ "id", "fitLevel" }]</c> as JSON.</summary>
    public string GarmentsJson { get; set; } = "[]";

    /// <summary>The measurements the mannequin guessed (<c>chest</c>, <c>hip</c>, ...).</summary>
    public string[] DefaultedMeasurements { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset LastUsedAt { get; set; }
}
