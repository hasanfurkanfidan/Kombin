using System.Text.Json;
using System.Text.Json.Serialization;
using Kombin.Api.Features.Catalog;
using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Profile;

namespace Kombin.Api.Persistence.Seed;

/// <summary>
/// One card of <c>style-cards.json</c>. <c>ImageUrl</c> defaults to a numbered placeholder and <c>SortOrder</c> to the position in
/// the file; real editorial images are a later content task and only need to fill <c>imageUrl</c>.
/// </summary>
internal sealed record StyleCardSeedItem(
    Guid Id,
    string Title,
    Audience Audience,
    StyleFamily Family,
    string[] Colors,
    short Formality,
    string[] Fits,
    string[] Seasons,
    string? ImageUrl = null,
    int? SortOrder = null,
    bool IsActive = true);

/// <summary>The embedded <c>style-cards.json</c>: editorial content that is seeded in every environment, unlike the development catalog.</summary>
internal sealed record StyleCardCatalog(IReadOnlyList<StyleCardSeedItem> Cards)
{
    private const string ResourceName = "Kombin.Api.Persistence.Seed.style-cards.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static StyleCardCatalog Load()
    {
        using var stream = typeof(StyleCardCatalog).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' not found.");

        return JsonSerializer.Deserialize<StyleCardCatalog>(stream, JsonOptions)
            ?? throw new InvalidOperationException($"'{ResourceName}' is empty.");
    }

    public IReadOnlyList<StyleCard> ToEntities() =>
        [.. Cards.Select((card, index) => new StyleCard
        {
            Id = card.Id,
            Title = card.Title,
            ImageUrl = card.ImageUrl ?? $"https://picsum.photos/seed/style-{index + 1}/600/800",
            Audience = card.Audience,
            Family = card.Family,
            Colors = [.. card.Colors],
            Formality = card.Formality,
            Fits = [.. card.Fits],
            Seasons = EnumNames.ParseSeasons(card.Seasons),
            SortOrder = card.SortOrder ?? index + 1,
            IsActive = card.IsActive,
        })];
}
