using System.Text;
using System.Text.RegularExpressions;
using Kombin.Api.Common.Text;
using Kombin.Api.Features.Recommendation.Narration;
using Kombin.Api.Features.Social;

namespace Kombin.Api.Tests;

/// <summary>
/// The app glossary for the Turkish copy the backend writes (<see cref="CopyGlossary"/>): every string literal of the sources below that can
/// reach a user is scanned for the banned words. Legal pages are excluded (their own review), and so are validator messages (they name request
/// fields). The posting consent is checked as published text, from version 2 on (<see cref="TheConsentKeepsToTheGlossaryFromVersionTwoOn"/>). Interpolation holes and placeholders (<c>{personaName}</c>) are not copy and are dropped before the check.
/// </summary>
public sealed partial class CopyGlossaryTests
{
    /// <summary>Copy that never talks about people: "takip" is banned there too (a price is "izlenir").</summary>
    public static TheoryData<string> AboutThings =>
    [
        "Features/Notification/Copy.cs",
        "Features/Recommendation/Narration/TemplateNarrator.cs",
        "Features/Recommendation/Narration/TurkishGrammar.cs",
        "Features/Recommendation/GetDaily/Endpoint.cs",
        "Features/Recommendation/GetHowToWear/Endpoint.cs",
        "Features/Recommendation/OutfitResponses.cs",
        "Features/Recommendation/BudgetOutfits.cs",
        "Features/Recommendation/TripPlanning.cs",
        "Features/Wardrobe/GapAnalysis.cs",
    ];

    /// <summary>
    /// Status messages, post copy and every problem detail. The posting consent's source holds version 1 too, which stays in history as published
    /// ("akışta"), so its versions are checked as texts instead of scanning the file.
    /// </summary>
    public static TheoryData<string> AboutAnything =>
    [
        "Features/Social/PostResponses.cs",
        .. Directory.EnumerateFiles(Path.Combine(SourceRoot(), "Features"), "*Errors.cs", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(SourceRoot(), path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal),
    ];

    [Theory]
    [MemberData(nameof(AboutThings))]
    public void CopyAboutThingsKeepsToTheGlossary(string file) => AssertClean(file, aboutPeople: false);

    [Theory]
    [MemberData(nameof(AboutAnything))]
    public void CopyKeepsToTheGlossary(string file) => AssertClean(file, aboutPeople: true);

    [Theory]
    [InlineData("%94 uyum", "uyum")]
    [InlineData("Uyum skoru", "uyum")]
    [InlineData("Lacivert ve gri komşu tonlar", "komşu")]
    [InlineData("Tek renkli vurgu", "vurgu")]
    [InlineData("NÖTR tonlar", "nötr")]
    [InlineData("Tonal bir görünüm", "tonal")]
    [InlineData("Dolabınla eşleşen parçalar", "eşleş")]
    [InlineData("Akışta yeni gönderiler var", "akış")]
    [InlineData("AKIŞTA", "akış")]
    [InlineData("Persona: Ofis", "persona")]
    public void TheBannedWordsAreCaught(string text, string word) => Assert.Contains(word, CopyGlossary.Violations(text));

    [Theory]
    [InlineData("Bu ceket tarzınla uyumlu")]
    [InlineData("Akış")]
    [InlineData("Sana uygun %94")]
    [InlineData("Takip et")]
    public void WhatTheGlossaryAllowsPasses(string text) => Assert.Empty(CopyGlossary.Violations(text));

    /// <summary>
    /// The posting consent follows the glossary from version 2 (2026-11-v2) on. Version 1 predates the rule and stays in history unchanged:
    /// published texts are pinned (<c>PostingConsentTests</c>) and every acceptance row stores the exact text that was shown.
    /// </summary>
    [Fact]
    public void TheConsentKeepsToTheGlossaryFromVersionTwoOn()
    {
        var checkedVersions = PostingConsent.Versions.Skip(1).ToList();
        Assert.NotEmpty(checkedVersions);

        foreach (var version in checkedVersions)
        {
            var words = CopyGlossary.Violations(version.Text);
            Assert.True(words.Count == 0, $"Consent {version.Version} uses banned words: {string.Join(", ", words)}");
        }
    }

    [Fact]
    public void TakipIsOnlyForPeople() => Assert.Equal(["takip"], CopyGlossary.Violations("Takip ettiğin ceket indirimde", aboutPeople: false));

    [Fact]
    public void TheNarratorPromptCarriesTheRuleAndALineThatBreaksItIsDropped()
    {
        Assert.All(CopyGlossary.Banned.Append(CopyGlossary.PeopleOnly), word => Assert.Contains(word, AnthropicNarrator.SystemPrompt, StringComparison.Ordinal));
        Assert.False(AnthropicNarrator.IsUsable("Lacivert ve gri komşu tonlar, sakin bir kombin."));
        Assert.True(AnthropicNarrator.IsUsable("Lacivert ve gri birbirine yakın renkler, sakin bir kombin."));
    }

    [Fact]
    public void TheLexerReadsEveryKindOfLiteralAndSkipsComments()
    {
        const string source = """"
            // "komşu" in a comment
            /* "vurgu" */ var a = "bir";
            var b = @"iki ""tırnak""";
            var c = $"üç {persona} dört";
            var d = '"';
            var e = $$"""
                beş {{x}}
                """;
            """";

        Assert.Equal(["bir", "iki \"tırnak\"", "üç  dört", "beş"], Literals(source).Select(literal => literal.Trim()));
    }

    private static void AssertClean(string file, bool aboutPeople)
    {
        var path = Path.Combine(SourceRoot(), file);
        Assert.True(File.Exists(path), $"{file} is gone: update the list in CopyGlossaryTests.");

        var problems = Literals(File.ReadAllText(path))
            .Where(literal => !ErrorCode().IsMatch(literal))
            .Select(literal => (Literal: literal, Words: CopyGlossary.Violations(literal, aboutPeople)))
            .Where(entry => entry.Words.Count > 0)
            .Select(entry => $"{string.Join(", ", entry.Words)}: \"{entry.Literal}\"")
            .ToList();

        Assert.True(problems.Count == 0, $"{file} uses banned words (kombin-frontend/docs/design/glossary.md):\n{string.Join('\n', problems)}");
    }

    /// <summary>The contents of every string literal in C# source, comments skipped, interpolation holes and <c>{placeholders}</c> removed.</summary>
    internal static List<string> Literals(string source)
    {
        var literals = new List<string>();
        var index = 0;

        while (index < source.Length)
        {
            var ch = source[index];

            if (ch == '/' && At(source, index + 1) == '/')
            {
                index = source.IndexOf('\n', index) is var end && end < 0 ? source.Length : end;
                continue;
            }

            if (ch == '/' && At(source, index + 1) == '*')
            {
                index = source.IndexOf("*/", index + 2, StringComparison.Ordinal) is var end && end < 0 ? source.Length : end + 2;
                continue;
            }

            if (ch == '\'')
            {
                // A char literal: '"' must not open a string.
                index++;
                while (index < source.Length && source[index] != '\'')
                {
                    index += source[index] == '\\' ? 2 : 1;
                }

                index++;
                continue;
            }

            if (ch is '"' or '$' or '@')
            {
                var start = index;
                var dollars = 0;
                var verbatim = false;
                while (index < source.Length && source[index] is '$' or '@')
                {
                    dollars += source[index] == '$' ? 1 : 0;
                    verbatim |= source[index] == '@';
                    index++;
                }

                if (At(source, index) != '"')
                {
                    index = start + 1;
                    continue;
                }

                var quotes = 0;
                while (At(source, index + quotes) == '"')
                {
                    quotes++;
                }

                string text;
                if (quotes >= 3)
                {
                    var close = new string('"', quotes);
                    var end = source.IndexOf(close, index + quotes, StringComparison.Ordinal);
                    end = end < 0 ? source.Length : end;
                    text = source[(index + quotes)..end];
                    index = end + quotes;
                }
                else
                {
                    var builder = new StringBuilder();
                    index++;
                    while (index < source.Length)
                    {
                        var current = source[index];
                        if (verbatim && current == '"' && At(source, index + 1) == '"')
                        {
                            builder.Append('"');
                            index += 2;
                        }
                        else if (!verbatim && current == '\\')
                        {
                            builder.Append(At(source, index + 1) == '"' ? '"' : ' ');
                            index += 2;
                        }
                        else if (current == '"')
                        {
                            index++;
                            break;
                        }
                        else
                        {
                            builder.Append(current);
                            index++;
                        }
                    }

                    text = builder.ToString();
                }

                literals.Add(Placeholder().Replace(text, string.Empty));
                continue;
            }

            index++;
        }

        return literals;
    }

    private static char At(string source, int index) => index < source.Length ? source[index] : '\0';

    private static string SourceRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Kombin.slnx")))
            {
                return Path.Combine(directory.FullName, "src", "Kombin.Api");
            }
        }

        throw new InvalidOperationException("Kombin.slnx not found above the test binaries.");
    }

    /// <summary>An interpolation hole or a template placeholder, nested braces included (<c>{{x}}</c>, <c>{a / 10 * 10}</c>).</summary>
    [GeneratedRegex(@"\{+[^{}""]*\}+")]
    private static partial Regex Placeholder();

    /// <summary>An error code such as <c>Profile.PersonaNotFound</c>: part of the API contract, not copy.</summary>
    [GeneratedRegex(@"^[A-Z][A-Za-z]+\.[A-Z][A-Za-z]+$")]
    private static partial Regex ErrorCode();
}
