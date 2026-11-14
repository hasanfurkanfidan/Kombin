using System.Globalization;
using System.Text.Json;
using Kombin.Api.Features.Notification;

namespace Kombin.Api.Features.Admin;

/// <summary>How the admin pages print times, money, sizes and moderation answers. Pure, so it is unit-tested.</summary>
internal static class AdminFormat
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    /// <summary>Times are shown in Istanbul (the founder's day, like the job schedules): <c>2026-11-14 18:30</c>.</summary>
    public static string Time(DateTimeOffset? value) =>
        value is { } time ? TimeZoneInfo.ConvertTime(time, QuietHours.Zone).ToString("yyyy-MM-dd HH:mm", Culture) : "-";

    public static string Money(decimal value) => string.Create(Culture, $"{value:N2} ₺");

    public static string Bytes(long? value)
    {
        if (value is not { } bytes)
        {
            return "-";
        }

        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var size = (double)bytes;
        var unit = 0;

        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return string.Create(Culture, $"{size:0.#} {units[unit]}");
    }

    /// <summary>"3 minutes ago", "2 h ago", "5 d ago": enough to tell a live job from a dead one.</summary>
    public static string Ago(DateTimeOffset? at, DateTimeOffset now)
    {
        if (at is not { } time)
        {
            return "never";
        }

        var span = now - time;

        return span < TimeSpan.Zero ? "in the future"
            : span < TimeSpan.FromMinutes(1) ? "just now"
            : span < TimeSpan.FromHours(1) ? string.Create(Culture, $"{(int)span.TotalMinutes} min ago")
            : span < TimeSpan.FromDays(1) ? string.Create(Culture, $"{(int)span.TotalHours} h ago")
            : string.Create(Culture, $"{(int)span.TotalDays} d ago");
    }

    /// <summary><c>f***@gmail.com</c>: enough to tell two accounts apart without printing the address (e-mails belong on the user search page only).</summary>
    public static string MaskEmail(string email)
    {
        var at = email.IndexOf('@', StringComparison.Ordinal);

        return at <= 0 ? "***" : string.Concat(email.AsSpan(0, 1), "***", email.AsSpan(at));
    }
}

/// <summary>One moderation answer of a post: its verdict, the labels that tripped and every score (<c>nsfw 0.91</c>).</summary>
internal sealed record ModerationLine(string Subject, string Verdict, IReadOnlyList<string> Labels, IReadOnlyList<KeyValuePair<string, double>> Scores);

/// <summary>
/// Reads <c>posts.moderation_json</c>, which has two shapes (<c>{ photo: {...} }</c> for a photo post, <c>{ pieces: { itemId: {...} } }</c> for an
/// outfit post; each answer is <c>{ verdict, labels, scores }</c>). Anything else parses to no lines: the page then shows the raw JSON only.
/// </summary>
internal static class ModerationView
{
    public static IReadOnlyList<ModerationLine> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var lines = new List<ModerationLine>();

            if (root.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            if (root.TryGetProperty("photo", out var photo) && photo.ValueKind == JsonValueKind.Object)
            {
                lines.Add(ReadLine("photo", photo));
            }

            if (root.TryGetProperty("pieces", out var pieces) && pieces.ValueKind == JsonValueKind.Object)
            {
                lines.AddRange(pieces.EnumerateObject().Where(piece => piece.Value.ValueKind == JsonValueKind.Object).Select(piece => ReadLine($"piece {piece.Name[..Math.Min(8, piece.Name.Length)]}", piece.Value)));
            }

            return lines;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static ModerationLine ReadLine(string subject, JsonElement answer)
    {
        var verdict = answer.TryGetProperty("verdict", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "?";
        var labels = answer.TryGetProperty("labels", out var l) && l.ValueKind == JsonValueKind.Array
            ? l.EnumerateArray().Where(label => label.ValueKind == JsonValueKind.String).Select(label => label.GetString()!).ToList()
            : [];
        var scores = answer.TryGetProperty("scores", out var s) && s.ValueKind == JsonValueKind.Object
            ? s.EnumerateObject().Where(score => score.Value.ValueKind == JsonValueKind.Number).Select(score => KeyValuePair.Create(score.Name, score.Value.GetDouble())).ToList()
            : [];

        return new ModerationLine(subject, verdict, labels, scores);
    }
}

/// <summary>The reasons the founder can pick when blocking a post; the author reads the text (Turkish, like every user-facing line of the Social feature).</summary>
internal static class BlockReasons
{
    public const string Default = "default";

    public static readonly IReadOnlyList<KeyValuePair<string, string>> All =
    [
        KeyValuePair.Create(Default, Social.SocialCopy.RemovedAfterReview),
        KeyValuePair.Create("nudity", "Gönderi çıplaklık veya müstehcen içerik nedeniyle kaldırıldı."),
        KeyValuePair.Create("violence", "Gönderi şiddet içeriği nedeniyle kaldırıldı."),
        KeyValuePair.Create("hate", "Gönderi nefret söylemi veya taciz nedeniyle kaldırıldı."),
        KeyValuePair.Create("spam", "Gönderi spam veya dolandırıcılık nedeniyle kaldırıldı."),
        KeyValuePair.Create("consent", "Gönderi, fotoğraftaki kişilerin izni olmadığı için kaldırıldı."),
    ];

    /// <summary>The list text for <paramref name="key"/> (the default for an unknown key) followed by the founder's own words, if any.</summary>
    public static string Compose(string? key, string? custom)
    {
        var listText = All.FirstOrDefault(reason => reason.Key == key).Value ?? All[0].Value;
        var extra = custom?.Trim();

        return string.IsNullOrEmpty(extra) ? listText : $"{listText} {extra}";
    }
}
