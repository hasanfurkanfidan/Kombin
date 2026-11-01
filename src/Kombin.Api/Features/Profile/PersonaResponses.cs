using Kombin.Api.Features.Catalog;
using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Profile;

namespace Kombin.Api.Features.Profile;

internal sealed record PersonaRef(Guid Id, string Name, string Icon);

/// <summary>What the app shows about a persona's learned taste. <c>TopColors</c> has up to 3 names, <c>TopFamilies</c> up to 2.</summary>
internal sealed record StyleSummary(
    double Confidence,
    int LikeCount,
    IReadOnlyList<string> TopColors,
    decimal FormalityMean,
    IReadOnlyList<string> TopFamilies)
{
    public static StyleSummary From(PersonaStyleProfile profile) => new(
        Math.Round(profile.Confidence, 2),
        profile.LikeCount,
        Top(profile.ColorWeights, 3),
        profile.FormalityMean,
        Top(profile.FamilyWeights, 2));

    private static string[] Top(Dictionary<string, double> weights, int count) =>
        [.. weights.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal).Take(count).Select(pair => pair.Key)];
}

internal sealed record PersonaResponse(
    Guid Id,
    string Name,
    string Icon,
    int SortOrder,
    decimal? BudgetMin,
    decimal? BudgetMax,
    int FormalityMin,
    int FormalityMax,
    IReadOnlyList<string> PreferredColors,
    IReadOnlyList<string> AvoidedColors,
    IReadOnlyList<string> PreferredFits,
    IReadOnlyList<string> Seasons,
    bool IsActive,
    StyleSummary StyleSummary)
{
    public static PersonaResponse From(Persona persona, PersonaStyleProfile styleProfile, bool isActive) => new(
        persona.Id,
        persona.Name,
        persona.Icon,
        persona.SortOrder,
        persona.BudgetMin,
        persona.BudgetMax,
        persona.FormalityMin,
        persona.FormalityMax,
        persona.PreferredColors,
        persona.AvoidedColors,
        persona.PreferredFits,
        EnumNames.SeasonNames(persona.Seasons),
        isActive,
        StyleSummary.From(styleProfile));
}

/// <summary>Request lists arrive as text; stored preferences are the canonical enum names, without duplicates.</summary>
internal static class PersonaPreferences
{
    public static string[] Canonical<TEnum>(IEnumerable<string> names)
        where TEnum : struct, Enum =>
        [.. names.Select(name => EnumNames.TryParse<TEnum>(name, out var value) ? value.ToString() : name).Distinct(StringComparer.Ordinal)];

    public static bool AreAll<TEnum>(IEnumerable<string> names)
        where TEnum : struct, Enum =>
        names.All(EnumNames.IsValid<TEnum>);

    public static string[] Colors(IEnumerable<string> names) => Canonical<ProductColor>(names);

    public static string[] Fits(IEnumerable<string> names) => Canonical<ProductFit>(names);
}
