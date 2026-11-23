using System.Security.Cryptography;
using Kombin.Api.Persistence.Entities.AskFriend;

namespace Kombin.Api.Features.AskFriend;

/// <summary>The link token of an ask: 16 random bytes as base64url, 22 characters. Unguessable, and it opens one page, never an account.</summary>
internal static class AskToken
{
    private const int ByteCount = 16;

    public static string New() => Base64Url(RandomNumberGenerator.GetBytes(ByteCount));

    /// <summary>Whether a route value can be a token at all (checked before any query).</summary>
    public static bool IsWellFormed(string? value) =>
        value is { Length: Ask.TokenLength } && value.All(character => character is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_');

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
