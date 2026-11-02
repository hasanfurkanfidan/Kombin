using System.Text.Json.Nodes;
using Kombin.Api.Features.Recommendation;
using Kombin.Api.Features.Recommendation.Narration;
using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Recommendation;

namespace Kombin.Api.Tests;

public class OutfitNarrationTests
{
    private static PieceSummary Piece(OutfitSlot slot, string slug, ProductColor color, bool gap = false, bool anchor = false, ProductFit fit = ProductFit.Regular) =>
        new(slot, slug, color, fit, ProductPattern.Solid, gap, anchor);

    private static OutfitSummary Summary(
        OutfitRule rule,
        ProductColor top = ProductColor.White,
        ProductColor bottom = ProductColor.Navy,
        bool outerwear = true,
        bool gapShoes = false,
        bool anchorTop = false,
        int? temperature = 9,
        Occasion? occasion = null,
        OutfitKind kind = OutfitKind.Daily) =>
        new(
            kind,
            rule,
            occasion,
            temperature is null ? null : TemperatureBand.Cool,
            rule == OutfitRule.Rain,
            temperature,
            [
                Piece(OutfitSlot.Top, "ust-giyim/gomlek", top, anchor: anchorTop, fit: ProductFit.Oversized),
                Piece(OutfitSlot.Bottom, "alt-giyim/pantolon", bottom),
                .. outerwear ? [Piece(OutfitSlot.Outerwear, "dis-giyim/kaban", ProductColor.Beige)] : Array.Empty<PieceSummary>(),
                Piece(OutfitSlot.Shoes, "ayakkabi/loafer", ProductColor.Brown, gap: gapShoes),
            ]);

    // ---- grammar ----

    [Theory]
    [InlineData("pantolon", "pantolonla")]
    [InlineData("gömlek", "gömlekle")]
    [InlineData("triko", "trikoyla")]
    [InlineData("çanta", "çantayla")]
    [InlineData("kemer", "kemerle")]
    [InlineData("etek", "etekle")]
    [InlineData("ayakkabı", "ayakkabıyla")]
    [InlineData("şort", "şortla")]
    public void TheIleSuffixFollowsVowelHarmony(string word, string expected)
    {
        Assert.Equal(expected, TurkishGrammar.WithSuffix(word));
    }

    [Theory]
    [InlineData("ust-giyim/blazer", "blazer", "blazer'la")]
    [InlineData("alt-giyim/jean", "jean", "jean'le")]
    [InlineData("ayakkabi/sneaker", "sneaker", "sneaker'la")]
    [InlineData("ust-giyim/tisort", "tişört", "tişörtle")]
    [InlineData("aksesuar/canta", "çanta", "çantayla")]
    [InlineData("ust-giyim/yelek", "üst", "üstle")]
    public void GarmentNounsKnowTheirLoanwordForms(string slug, string noun, string with)
    {
        Assert.Equal(noun, TurkishGrammar.Noun(slug));
        Assert.Equal(with, TurkishGrammar.NounWith(slug));
    }

    [Theory]
    [InlineData("ilk", "İlk")]
    [InlineData("ılık", "Ilık")]
    [InlineData("lacivert", "Lacivert")]
    [InlineData("", "")]
    public void CapitalisationIsTurkish(string text, string expected)
    {
        Assert.Equal(expected, TurkishGrammar.Capitalize(text));
    }

    [Fact]
    public void PhrasesPutTheColourBeforeTheNoun()
    {
        Assert.Equal("lacivert pantolon", TurkishGrammar.Phrase(ProductColor.Navy, "alt-giyim/pantolon"));
        Assert.Equal("kahverengi loafer'la", TurkishGrammar.PhraseWith(ProductColor.Brown, "ayakkabi/loafer"));
        Assert.Equal("pantolon", TurkishGrammar.Phrase(ProductColor.Other, "alt-giyim/pantolon"));
    }

    // ---- templates ----

    [Fact]
    public void ThereAreAtLeastTwelveTemplatesAndEveryRuleHasOne()
    {
        Assert.True(TemplateNarrator.Templates.Count >= 12);
        Assert.All(Enum.GetValues<OutfitRule>(), rule => Assert.Contains(TemplateNarrator.Templates, entry => entry.Rule == rule));
    }

    [Fact]
    public void EveryTemplateRendersToOneCleanTurkishSentence()
    {
        foreach (var rule in Enum.GetValues<OutfitRule>())
        {
            var summary = Summary(rule, top: ProductColor.Red, bottom: ProductColor.Green, gapShoes: rule == OutfitRule.Gap, anchorTop: rule == OutfitRule.Anchor);
            var line = TemplateNarrator.Narrate(summary);

            Assert.DoesNotContain("{", line, StringComparison.Ordinal);
            Assert.DoesNotContain("  ", line, StringComparison.Ordinal);
            Assert.True(char.IsUpper(line[0]), line);
            Assert.EndsWith(".", line, StringComparison.Ordinal);
            Assert.InRange(line.Length, 20, Outfit.MaxNarrationLength);
        }
    }

    [Fact]
    public void TheLineIsStablePerOutfitAndVariesAcrossOutfits()
    {
        var summary = Summary(OutfitRule.AllNeutral);

        Assert.Equal(TemplateNarrator.Narrate(summary), TemplateNarrator.Narrate(summary));

        var lines = Enum.GetValues<ProductColor>()
            .SelectMany(top => new[] { ProductColor.Navy, ProductColor.Black, ProductColor.Beige }.Select(bottom => Summary(OutfitRule.AllNeutral, top, bottom)))
            .Select(TemplateNarrator.Narrate)
            .ToList();
        var openings = lines.Select(line => line.Split(':')[0]).Distinct().Count();

        Assert.True(openings >= 2, "both all-neutral templates should be used");
    }

    [Fact]
    public void LinesTalkAboutWhatMadeTheOutfitWork()
    {
        var rain = TemplateNarrator.Narrate(Summary(OutfitRule.Rain));
        Assert.True(rain.Contains("Yağmur", StringComparison.Ordinal) || rain.Contains("yağmur", StringComparison.Ordinal), rain);
        Assert.Contains("bej kaban", rain, StringComparison.Ordinal);

        var gap = TemplateNarrator.Narrate(Summary(OutfitRule.Gap, gapShoes: true));
        Assert.Contains("kahverengi loafer", gap, StringComparison.OrdinalIgnoreCase);

        var complementary = TemplateNarrator.Narrate(Summary(OutfitRule.Complementary, ProductColor.Red, ProductColor.Green));
        Assert.Equal("Kırmızı ile yeşil zıt renkler ama birbirini parlatıyor.", complementary);

        var silhouette = TemplateNarrator.Narrate(Summary(OutfitRule.Silhouette));
        Assert.Equal("Bol kesim beyaz gömlek lacivert pantolonla dengeleniyor; oranlar tam yerinde.", silhouette);

        var anchor = TemplateNarrator.Narrate(Summary(OutfitRule.Anchor, anchorTop: true, kind: OutfitKind.HowToWear));
        Assert.StartsWith("Beyaz gömlek", anchor, StringComparison.Ordinal);
    }

    [Fact]
    public void ATemplateWhoseTokensAreMissingFallsBackToTheNeutralOnes()
    {
        // Cold without a layer or a temperature cannot be told; the line still comes out.
        var line = TemplateNarrator.Narrate(Summary(OutfitRule.Cold, outerwear: false, temperature: null));

        Assert.Contains(TemplateNarrator.Templates.Where(entry => entry.Rule == OutfitRule.AllNeutral), entry => line.StartsWith(entry.Template[..10], StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheTemplateNarratorAnswersInOrder()
    {
        var narrator = new TemplateNarrator();
        var summaries = new[] { Summary(OutfitRule.Rain), Summary(OutfitRule.Hot) };

        var lines = await narrator.NarrateAsync(summaries, TestContext.Current.CancellationToken);

        Assert.Equal(summaries.Select(TemplateNarrator.Narrate), lines.Select(line => line.Text));
        Assert.All(lines, line => Assert.Equal(NarrationProvider.Template, line.Provider));
    }

    [Fact]
    public void TheSummaryRoundTripsAndItsHashIgnoresNothing()
    {
        var summary = Summary(OutfitRule.OneStatement, ProductColor.Red);

        Assert.Equal(summary.Hash, OutfitSummary.FromJson(summary.ToJson())!.Hash);
        Assert.NotEqual(summary.Hash, (summary with { Rain = true }).Hash);
        Assert.Equal(32, summary.Hash.Length);
    }

    // ---- Anthropic contract (the HTTP side is tested against a fake handler in the integration tests) ----

    [Fact]
    public void TheRequestAsksForStrictJsonWithSixtyTokensPerOutfit()
    {
        var request = AnthropicNarrator.BuildRequest([Summary(OutfitRule.Rain), Summary(OutfitRule.Hot), Summary(OutfitRule.Gap, gapShoes: true)], "claude-haiku-5-5");

        Assert.Equal("claude-haiku-5-5", request["model"]!.GetValue<string>());
        Assert.Equal(180, request["max_tokens"]!.GetValue<int>());
        Assert.Equal("disabled", request["thinking"]!["type"]!.GetValue<string>());
        Assert.Equal("json_schema", request["output_config"]!["format"]!["type"]!.GetValue<string>());
        Assert.False(request["output_config"]!["format"]!["schema"]!["additionalProperties"]!.GetValue<bool>());
        Assert.Null(request["temperature"]);

        var input = JsonNode.Parse(request["messages"]![0]!["content"]!.GetValue<string>())!.AsArray();
        Assert.Equal(["0", "1", "2"], input.Select(outfit => outfit!["id"]!.GetValue<string>()));
        Assert.Equal("Rain", input[0]!["rule"]!.GetValue<string>());
        Assert.Equal("kaban", input[0]!["pieces"]![2]!["garment"]!.GetValue<string>());
        Assert.True(input[2]!["pieces"]![3]!["fromCatalog"]!.GetValue<bool>());
    }

    [Fact]
    public void AnswersAreReadOnlyWhenCompleteAndSchemaShaped()
    {
        static string Message(string stop, string text) =>
            new JsonObject { ["stop_reason"] = stop, ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }) }.ToJsonString();

        var good = AnthropicNarrator.ParseResponse(Message("end_turn", """{"lines":[{"id":"0","text":"Yağmura hazırsın."},{"id":"1","text":"Serin kal."}]}"""));
        Assert.NotNull(good);
        Assert.Equal("Yağmura hazırsın.", good["0"]);
        Assert.Equal("Serin kal.", good["1"]);

        Assert.Null(AnthropicNarrator.ParseResponse(Message("max_tokens", """{"lines":[{"id":"0","text":"Yağ""")));
        Assert.Null(AnthropicNarrator.ParseResponse(Message("end_turn", "not json")));
        Assert.Null(AnthropicNarrator.ParseResponse(Message("refusal", """{"lines":[]}""")));
        Assert.Null(AnthropicNarrator.ParseResponse("<html>"));
    }
}
