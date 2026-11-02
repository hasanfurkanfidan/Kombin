using System.Globalization;
using System.Text.RegularExpressions;
using Kombin.Api.Persistence.Entities.Recommendation;

namespace Kombin.Api.Features.Recommendation.Narration;

/// <summary>
/// The offline narrator: one Turkish sentence per outfit, from templates keyed by the rule that made the outfit work. Which template
/// of a rule is used depends on the summary's hash, so an outfit always gets the same line and different outfits get different ones.
/// A template is used only when every token it names can be filled; otherwise the all-neutral templates, which only need the top,
/// bottom and shoes, take over.
/// </summary>
internal sealed partial class TemplateNarrator : IOutfitNarrator
{
    /// <summary>Tokens: {top} {bottom} {shoes} {outer} and their "ile" forms ({bottomWith} ...), {statement} {base} {colorA} {colorB} {color} {temp} {gap} {anchor} {companions}.</summary>
    public static readonly IReadOnlyList<(OutfitRule Rule, string Template)> Templates =
    [
        (OutfitRule.Rain, "Yağmur bekleniyor: {shoes} ve {outer} ile ıslanmadan şık kal."),
        (OutfitRule.Rain, "Şemsiyeni unutma; {outer} ve {shoes} bugün yağmura hazır."),
        (OutfitRule.Cold, "Hava {temp}°C; {outer} seni sıcak tutar, altında {top} rahat."),
        (OutfitRule.Cold, "Serin bir gün için katman katman: {outer}, {top} ve {bottom}."),
        (OutfitRule.Hot, "Sıcak bir gün: hafif {top} ve {bottom} ile serin kal."),
        (OutfitRule.Hot, "Hava {temp}°C: {top}, {bottom} ve {shoes} ile hafif ve rahat."),
        (OutfitRule.Gap, "Tek eksik {gap}; gerisi zaten dolabında."),
        (OutfitRule.Gap, "{gap} bu kombini tamamlıyor, diğer parçalar senden."),
        (OutfitRule.Complementary, "{colorA} ile {colorB} zıt renkler ama birbirini parlatıyor."),
        (OutfitRule.Analogous, "{colorA} ve {colorB} komşu tonlar; göz yormadan renkli bir kombin."),
        (OutfitRule.Monochrome, "Baştan aşağı {color} tonları: zahmetsiz ve boyunu uzun gösteren bir görünüm."),
        (OutfitRule.OneStatement, "{statement} bugünün yıldızı, {base} ise ona sahneyi bırakıyor."),
        (OutfitRule.OneStatement, "Tek renkli vurgu yeter: {statement} konuşsun, gerisi sakin kalsın."),
        (OutfitRule.Silhouette, "Bol kesim {top} {bottomWith} dengeleniyor; oranlar tam yerinde."),
        (OutfitRule.SmallPatterns, "İki ince desen yan yana ama birbirini bastırmıyor; gerisi sade."),
        (OutfitRule.Anchor, "{anchor} merkezde; {companions} onu sakin bir zeminde öne çıkarıyor."),
        (OutfitRule.Anchor, "{anchor} için kolay formül: {companions}."),
        (OutfitRule.Office, "Ofiste ciddi ama sıkıcı değil: {top} ve {bottom} tam kararında."),
        (OutfitRule.Weekend, "Hafta sonu rahatlığı: {top}, {bottom} ve {shoes} ile gün boyu konfor."),
        (OutfitRule.Evening, "Akşam için iddialı ama abartısız: {top} ve {bottom} şık bir ikili."),
        (OutfitRule.Sport, "Harekete hazır: {shoes} ve rahat bir {top} ile spora."),
        (OutfitRule.Event, "Davette göz dolduracak: {top}, {bottom} ve {shoes} aynı ciddiyette."),
        (OutfitRule.QuietNeutrals, "Koyu tonlar bir arada: {top} ve {bottom} sakin ve derli toplu."),
        (OutfitRule.AllNeutral, "Nötr tonlarla güvenli liman: {top} ve {bottom} her yere uyar."),
        (OutfitRule.AllNeutral, "Sade ama özenli: {top}, {bottom} ve {shoes} birbirini tamamlıyor."),
    ];

    public Task<IReadOnlyList<OutfitLine>> NarrateAsync(IReadOnlyList<OutfitSummary> outfits, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outfits);

        return Task.FromResult<IReadOnlyList<OutfitLine>>([.. outfits.Select(outfit => new OutfitLine(Narrate(outfit), NarrationProvider.Template))]);
    }

    public static string Narrate(OutfitSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        var tokens = Tokens(summary);
        var usable = Usable(summary.Rule, tokens);
        if (usable.Count == 0)
        {
            usable = Usable(OutfitRule.AllNeutral, tokens);
        }

        var template = usable[(int)(Fnv(summary.Hash) % (uint)usable.Count)];
        var text = TokenPattern().Replace(template, match => tokens[match.Groups[1].Value]);

        return TurkishGrammar.Capitalize(text);
    }

    private static List<string> Usable(OutfitRule rule, Dictionary<string, string> tokens) =>
        [.. Templates
            .Where(entry => entry.Rule == rule)
            .Select(entry => entry.Template)
            .Where(template => TokenPattern().Matches(template).All(match => tokens.ContainsKey(match.Groups[1].Value)))];

    private static Dictionary<string, string> Tokens(OutfitSummary summary)
    {
        var tokens = new Dictionary<string, string>(StringComparer.Ordinal);
        var pieces = summary.Pieces;

        foreach (var (slot, name) in new[] { (OutfitSlot.Top, "top"), (OutfitSlot.Bottom, "bottom"), (OutfitSlot.Shoes, "shoes"), (OutfitSlot.Outerwear, "outer") })
        {
            if (pieces.FirstOrDefault(piece => piece.Slot == slot) is { } piece)
            {
                tokens[name] = TurkishGrammar.Phrase(piece.Color, piece.CategorySlug);
                tokens[name + "With"] = TurkishGrammar.PhraseWith(piece.Color, piece.CategorySlug);
            }
        }

        var core = pieces.Where(piece => piece.Slot != OutfitSlot.Accessory).ToList();
        var coloured = core.Where(piece => !ColorRules.IsNeutral(piece.Color, Leaf(piece.CategorySlug))).ToList();
        var colours = coloured.Select(piece => piece.Color).Distinct().ToList();

        if (coloured.Count > 0)
        {
            tokens["statement"] = TurkishGrammar.Phrase(coloured[0].Color, coloured[0].CategorySlug);
            tokens["color"] = TurkishGrammar.Colour(coloured[0].Color);
        }

        if (core.FirstOrDefault(piece => piece.Slot is OutfitSlot.Bottom or OutfitSlot.Top && ColorRules.IsNeutral(piece.Color, Leaf(piece.CategorySlug))) is { } neutral)
        {
            tokens["base"] = TurkishGrammar.Phrase(neutral.Color, neutral.CategorySlug);
        }

        if (colours.Count == 2)
        {
            tokens["colorA"] = TurkishGrammar.Colour(colours[0]);
            tokens["colorB"] = TurkishGrammar.Colour(colours[1]);
        }

        if (summary.TemperatureC is { } temperature)
        {
            tokens["temp"] = temperature.ToString(CultureInfo.InvariantCulture);
        }

        if (pieces.FirstOrDefault(piece => piece.IsGap) is { } gap)
        {
            tokens["gap"] = TurkishGrammar.Phrase(gap.Color, gap.CategorySlug);
        }

        if (pieces.FirstOrDefault(piece => piece.IsAnchor) is { } anchor)
        {
            tokens["anchor"] = TurkishGrammar.Phrase(anchor.Color, anchor.CategorySlug);

            var companions = core.Where(piece => !piece.IsAnchor && piece.Slot != OutfitSlot.Shoes).Select(piece => TurkishGrammar.Phrase(piece.Color, piece.CategorySlug)).ToList();
            if (companions.Count > 0)
            {
                tokens["companions"] = companions.Count == 1 ? companions[0] : $"{string.Join(", ", companions[..^1])} ve {companions[^1]}";
            }
        }

        return tokens;
    }

    private static string Leaf(string categorySlug) => categorySlug[(categorySlug.LastIndexOf('/') + 1)..];

    private static uint Fnv(string text)
    {
        var value = 2166136261u;
        foreach (var character in text)
        {
            value = unchecked((value ^ character) * 16777619u);
        }

        return value;
    }

    [GeneratedRegex(@"\{(\w+)\}")]
    private static partial Regex TokenPattern();
}
