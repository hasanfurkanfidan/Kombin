namespace Kombin.Api.Persistence.Entities.TryOn;

/// <summary>
/// "Yanlış görünüyor": a user says a mannequin drawing looks wrong (a top shown from the back, most often). Exactly one of the outfit and the
/// product; <c>GarmentIds</c> are <c>tryon.garments</c> rows. No review screen yet: <c>docs/metrics.sql</c> counts them per garment.
/// </summary>
internal sealed class TryOnReport
{
    public long Id { get; set; }

    public Guid UserId { get; set; }

    public Guid? OutfitId { get; set; }

    public Guid? ProductId { get; set; }

    public Guid[] GarmentIds { get; set; } = [];

    public string Renderer { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }
}
