namespace Kombin.Api.Common.Text;

/// <summary>
/// The app glossary's banned words (<c>kombin-frontend/docs/design/glossary.md</c>, "Banned in UI copy"), for Turkish copy the backend writes:
/// notifications, outfit narration, post status messages, recommendation and gap messages, problem details. One word per thing everywhere in
/// the app: a match score is "Sana uygun %N", a persona is a "tarz", a price is "izlenir" (never "takip": that is only about people), and colour
/// theory is said in everyday words. <c>CopyGlossaryTests</c> scans those sources; the LLM narrator's prompt is built from <see cref="Banned"/>
/// and a line that still uses one is dropped for the template's.
/// </summary>
internal static class CopyGlossary
{
    /// <summary>Banned anywhere in user-facing copy (matched case-insensitively, Turkish casing folded by hand).</summary>
    public static readonly IReadOnlyList<string> Banned = ["persona", "uyum", "komşu", "vurgu", "nötr", "tonal", "eşleş", "akış"];

    /// <summary>"takip" is about following people only; copy that never talks about people (prices, outfits) must not use it.</summary>
    public const string PeopleOnly = "takip";

    /// <summary>The banned words <paramref name="text"/> uses: <c>uyum</c> only alone ("uyumlu" is fine), <c>akış</c> only as the feed tab's name.</summary>
    public static IReadOnlyList<string> Violations(string text, bool aboutPeople = true)
    {
        ArgumentNullException.ThrowIfNull(text);

        var folded = Fold(text);
        var found = new List<string>();

        foreach (var word in Banned)
        {
            var hit = word switch
            {
                "uyum" => ContainsWord(folded, word),
                "akış" => folded.Contains(word, StringComparison.Ordinal) && text.Trim() != "Akış",
                _ => folded.Contains(word, StringComparison.Ordinal),
            };

            if (hit)
            {
                found.Add(word);
            }
        }

        if (!aboutPeople && folded.Contains(PeopleOnly, StringComparison.Ordinal))
        {
            found.Add(PeopleOnly);
        }

        return found;
    }

    /// <summary>Lower case the Turkish way without a culture (the runtime may have invariant globalization): I → ı, İ → i.</summary>
    public static string Fold(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return text.Replace('I', 'ı').Replace('İ', 'i').ToLowerInvariant();
    }

    private static bool ContainsWord(string folded, string word)
    {
        for (var start = folded.IndexOf(word, StringComparison.Ordinal); start >= 0; start = folded.IndexOf(word, start + 1, StringComparison.Ordinal))
        {
            var end = start + word.Length;
            var before = start == 0 || !char.IsLetter(folded[start - 1]);
            var after = end == folded.Length || !char.IsLetter(folded[end]);

            if (before && after)
            {
                return true;
            }
        }

        return false;
    }
}
