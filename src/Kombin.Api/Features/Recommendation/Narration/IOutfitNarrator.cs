using System.Text.Json;
using System.Text.Json.Serialization;
using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Recommendation;

namespace Kombin.Api.Features.Recommendation.Narration;

internal sealed record PieceSummary(OutfitSlot Slot, string CategorySlug, ProductColor Color, ProductFit Fit, ProductPattern Pattern, bool IsGap, bool IsAnchor);

/// <summary>
/// What a narrator may know about an outfit: the pieces, the rule that made it work, the occasion and the weather. No names, brands or
/// prices, so the same summary always means the same line and the line can be cached by <see cref="Hash"/>.
/// </summary>
internal sealed record OutfitSummary(
    OutfitKind Kind,
    OutfitRule Rule,
    Occasion? Occasion,
    TemperatureBand? Band,
    bool Rain,
    int? TemperatureC,
    IReadOnlyList<PieceSummary> Pieces)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static OutfitSummary? FromJson(string? json) => string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<OutfitSummary>(json, Json);

    /// <summary>The narration cache key.</summary>
    [JsonIgnore]
    public string Hash => OutfitHash.Of(ToJson());

    public static OutfitSummary From(OutfitCandidate candidate, OutfitKind kind, OutfitContext context, string? anchorKey)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(context);

        return new OutfitSummary(
            kind,
            candidate.Evaluation.Rule,
            context.Occasion,
            context.Weather.Band,
            context.Weather.Rain,
            context.Weather.TemperatureC,
            [
                .. candidate.Pieces
                    .OrderBy(piece => piece.Slot)
                    .Select(piece => new PieceSummary(
                        piece.Slot,
                        piece.CategorySlug,
                        piece.Color,
                        piece.Fit,
                        piece.Pattern,
                        piece.Source == PieceSource.Catalog && piece.Key != anchorKey,
                        piece.Key == anchorKey)),
            ]);
    }
}

internal sealed record OutfitLine(string Text, NarrationProvider Provider);

/// <summary>Writes the one-line reason of each outfit, in order. Never throws for a failed provider: it falls back to the templates.</summary>
internal interface IOutfitNarrator
{
    Task<IReadOnlyList<OutfitLine>> NarrateAsync(IReadOnlyList<OutfitSummary> outfits, CancellationToken cancellationToken);
}

internal sealed class NarrationOptions
{
    public const string SectionName = "Narration";

    /// <summary><c>Template</c> (default) or <c>Anthropic</c>; with Anthropic the template line is upgraded in the background.</summary>
    public NarrationProvider Provider { get; set; } = NarrationProvider.Template;
}
