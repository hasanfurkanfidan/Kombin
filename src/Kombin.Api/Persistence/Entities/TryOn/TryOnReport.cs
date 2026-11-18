namespace Kombin.Api.Persistence.Entities.TryOn;

/// <summary>
/// "Yanlış görünüyor": a user says a mannequin drawing looks wrong (a top shown from the back, most often); it must be about one of the user's own
/// drawings (<see cref="RenderId"/>). Exactly one of the outfit and the product; <c>GarmentIds</c> are <c>tryon.garments</c> rows. No review screen yet: <c>docs/metrics.sql</c> counts them per garment.
/// </summary>
internal sealed class TryOnReport
{
    public long Id { get; set; }

    public Guid UserId { get; set; }

    public Guid? OutfitId { get; set; }

    public Guid? ProductId { get; set; }

    public Guid[] GarmentIds { get; set; } = [];

    /// <summary>
    /// The drawing the user reported (<see cref="TryOnRender"/>): one report per user and drawing. Null for a report of a drawing that was deleted since
    /// (other measurements, unused for 30 days, ...: the report stays, it is what the operators count).
    /// </summary>
    public Guid? RenderId { get; set; }

    public string Renderer { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }
}
