using Kombin.Api.Features.Recommendation;
using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Recommendation;

namespace Kombin.Api.Tests;

/// <summary>Synthetic wardrobe pieces for the outfit engine tests. IDs are derived from a name so tests stay deterministic.</summary>
internal static class OutfitPieces
{
    public static OutfitPiece Piece(
        string categorySlug,
        ProductColor color,
        int formality = 2,
        ProductPattern pattern = ProductPattern.Solid,
        ProductFit fit = ProductFit.Regular,
        Season seasons = Season.AllYear,
        string? name = null,
        PieceSource source = PieceSource.Wardrobe,
        int personaScore = 50,
        DateOnly? lastWornAt = null,
        decimal? price = null) =>
        new(
            source,
            IdOf(name ?? $"{categorySlug}-{color}-{formality}-{pattern}-{fit}"),
            OutfitSlots.ForCategorySlug(categorySlug),
            categorySlug,
            color,
            pattern,
            fit,
            formality,
            seasons,
            personaScore,
            lastWornAt,
            source == PieceSource.Catalog ? IdOf($"offer-{name ?? categorySlug}") : null,
            price);

    public static OutfitPiece Tee(ProductColor color, int formality = 2, string? name = null, ProductFit fit = ProductFit.Regular, ProductPattern pattern = ProductPattern.Solid) =>
        Piece("ust-giyim/tisort", color, formality, pattern, fit, name: name);

    public static OutfitPiece Shirt(ProductColor color, int formality = 3, string? name = null, ProductPattern pattern = ProductPattern.Solid) =>
        Piece("ust-giyim/gomlek", color, formality, pattern, name: name);

    public static OutfitPiece Trousers(ProductColor color, int formality = 3, string? name = null, ProductFit fit = ProductFit.Regular, ProductPattern pattern = ProductPattern.Solid) =>
        Piece("alt-giyim/pantolon", color, formality, pattern, fit, name: name);

    public static OutfitPiece Jeans(ProductColor color = ProductColor.Blue, int formality = 2, string? name = null, ProductFit fit = ProductFit.Slim) =>
        Piece("alt-giyim/jean", color, formality, fit: fit, name: name);

    public static OutfitPiece Sneaker(ProductColor color, int formality = 2, string? name = null) => Piece("ayakkabi/sneaker", color, formality, name: name);

    public static OutfitPiece Loafer(ProductColor color, int formality = 4, string? name = null) => Piece("ayakkabi/loafer", color, formality, name: name);

    public static OutfitPiece Boot(ProductColor color, int formality = 3, string? name = null) => Piece("ayakkabi/bot", color, formality, name: name);

    public static OutfitPiece Coat(ProductColor color, int formality = 3, string? name = null) => Piece("dis-giyim/kaban", color, formality, name: name);

    public static OutfitPiece Jacket(ProductColor color, int formality = 3, string? name = null) => Piece("dis-giyim/ceket", color, formality, name: name);

    public static OutfitPiece Blazer(ProductColor color, int formality = 4, string? name = null) => Piece("ust-giyim/blazer", color, formality, name: name);

    public static Guid IdOf(string name) => new(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name)).AsSpan(0, 16));

    public static readonly DateOnly Day = new(2026, 10, 15);

    /// <summary>A mild autumn day without a forecast, no occasion, no persona: only the outfit rules speak.</summary>
    public static OutfitContext Context(Occasion? occasion = null, WeatherConditions? weather = null, PersonaTaste? taste = null) =>
        OutfitContext.Create(occasion, taste ?? PersonaTaste.None, weather ?? WeatherRules.FromSeason(Day), Day);
}
