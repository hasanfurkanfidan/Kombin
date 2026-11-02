using System.Text.Json;
using Kombin.Api.Features.Catalog;
using Kombin.Api.Persistence.Entities.Catalog;

namespace Kombin.Api.Features.Wardrobe;

/// <summary>What is kept of a vision answer in <c>AnalysisJson</c>: everything except the images and the embedding (stored on their own).</summary>
internal sealed record StoredAnalysis(
    VisionCategory? Category,
    IReadOnlyList<VisionCategory> CategoryCandidates,
    VisionColor PrimaryColor,
    VisionColor? SecondaryColor,
    VisionPattern Pattern,
    VisionQuality Quality,
    string? EmbeddingModel,
    DateTimeOffset AnalyzedAt);

/// <summary>The item attributes an analysis leads to. <c>CategorySlug</c> is null when the top candidate was not sure enough.</summary>
internal sealed record AnalysedAttributes(
    string? CategorySlug,
    ProductColor PrimaryColor,
    ProductColor? SecondaryColor,
    ProductPattern Pattern,
    short Formality,
    Season Seasons,
    string[] Warnings);

/// <summary>Turns a vision answer into item attributes, without any I/O.</summary>
internal static class WardrobeAnalysis
{
    /// <summary>
    /// The category is taken only when the top candidate reaches this probability (a softmax over 15 classes, so 0.35 is already a
    /// clear winner); below it the item is ready without a category and the app shows the three candidates as chips.
    /// </summary>
    public const double CategoryThreshold = 0.35;

    public const string NotGarmentWarning = "not_garment";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static AnalysedAttributes Map(VisionAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);

        var category = AcceptedCategory(analysis.Category ?? (analysis.CategoryCandidates.Count > 0 ? analysis.CategoryCandidates[0] : null));
        var (formality, seasons) = Defaults(category);

        return new AnalysedAttributes(
            category,
            ParseColor(analysis.PrimaryColor.Name),
            analysis.SecondaryColor is { } secondary ? ParseColor(secondary.Name) : null,
            EnumNames.TryParse<ProductPattern>(analysis.Pattern.Name, out var pattern) ? pattern : ProductPattern.Other,
            formality,
            seasons,
            Warnings(analysis.Quality));
    }

    /// <summary>The slug of <paramref name="category"/> when its confidence reaches <see cref="CategoryThreshold"/>, otherwise null.</summary>
    public static string? AcceptedCategory(VisionCategory? category) =>
        category is { Confidence: >= CategoryThreshold } && !string.IsNullOrWhiteSpace(category.Slug) ? category.Slug : null;

    /// <summary>An unknown color name (a newer vision service) becomes <see cref="ProductColor.Other"/> rather than failing the analysis.</summary>
    public static ProductColor ParseColor(string? name) => EnumNames.TryParse<ProductColor>(name, out var color) ? color : ProductColor.Other;

    /// <summary><c>not_garment</c> first when the photo is not of clothing, then the service's own warnings. Warnings never block the item.</summary>
    public static string[] Warnings(VisionQuality quality)
    {
        ArgumentNullException.ThrowIfNull(quality);

        return [.. (quality.IsGarment ? [] : new[] { NotGarmentWarning }).Concat(quality.Warnings ?? []).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// A first guess at formality and seasons from the category, so the item is useful before the user edits it: sneakers and jeans are
    /// casual (2), blazers and loafers dressy (4), everything else 3; coats, jackets and boots are for autumn and winter, the rest all year.
    /// </summary>
    public static (short Formality, Season Seasons) Defaults(string? categorySlug)
    {
        var leaf = categorySlug is null ? null : categorySlug[(categorySlug.LastIndexOf('/') + 1)..];

        short formality = leaf switch
        {
            "tisort" or "jean" or "sneaker" or "mont" => 2,
            "blazer" or "loafer" => 4,
            _ => 3,
        };

        var seasons = leaf switch
        {
            "mont" or "kaban" or "bot" => Season.Autumn | Season.Winter,
            "triko" => Season.Autumn | Season.Winter | Season.Spring,
            _ => Season.AllYear,
        };

        return (formality, seasons);
    }

    public static string ToJson(VisionAnalysis analysis, string? embeddingModel, DateTimeOffset analyzedAt)
    {
        ArgumentNullException.ThrowIfNull(analysis);

        return JsonSerializer.Serialize(
            new StoredAnalysis(
                analysis.Category,
                analysis.CategoryCandidates,
                analysis.PrimaryColor,
                analysis.SecondaryColor,
                analysis.Pattern,
                analysis.Quality,
                embeddingModel,
                analyzedAt),
            Json);
    }

    /// <summary>Null for no analysis, or one stored in a shape this version cannot read.</summary>
    public static StoredAnalysis? Read(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<StoredAnalysis>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
