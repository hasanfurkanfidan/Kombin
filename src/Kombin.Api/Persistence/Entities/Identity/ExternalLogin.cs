namespace Kombin.Api.Persistence.Entities.Identity;

/// <summary>
/// One way into an account through an identity provider: the provider's stable user ID (<c>sub</c>) bound to our user.
/// No provider access or refresh token is ever stored.
/// </summary>
internal sealed class ExternalLogin : BaseEntity
{
    public Guid UserId { get; set; }

    /// <summary><c>google</c> or <c>apple</c> (<see cref="ExternalProviders"/>).</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>The token's <c>sub</c> claim; unique per provider and never reassigned by it.</summary>
    public string Subject { get; set; } = string.Empty;

    /// <summary>The verified email the token carried when the link was made (kept for support; the user's email may change later).</summary>
    public string? EmailAtLink { get; set; }
}

/// <summary>The provider names stored in <see cref="ExternalLogin.Provider"/> and accepted by <c>POST /auth/external</c>.</summary>
internal static class ExternalProviders
{
    public const string Google = "google";

    public const string Apple = "apple";

    public static readonly IReadOnlyList<string> All = [Google, Apple];
}
