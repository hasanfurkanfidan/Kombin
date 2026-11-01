using System.Text.RegularExpressions;

namespace Kombin.Api.Features.Notification;

internal static partial class PushTokens
{
    public const int MaxLength = 200;

    /// <summary>Expo tokens look like <c>ExponentPushToken[xxxxxxxxxxxxxxxxxxxxxx]</c>; newer SDKs say <c>ExpoPushToken[...]</c>.</summary>
    public static bool IsValid(string? token) =>
        token is { Length: > 0 and <= MaxLength } && TokenPattern().IsMatch(token);

    [GeneratedRegex(@"^(ExponentPushToken|ExpoPushToken)\[[^\[\]\s]+\]$", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();
}
