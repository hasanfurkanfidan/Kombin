namespace Kombin.Api.Persistence.Entities.Identity;

internal sealed class User : BaseEntity
{
    /// <summary>Always stored trimmed and lower-cased so the unique index is case-insensitive.</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>Null for an account created through Google or Apple; such a user can only sign in through an <see cref="ExternalLogin"/>.</summary>
    public string? PasswordHash { get; set; }

    public string DisplayName { get; set; } = string.Empty;

    public DateTimeOffset? LastLoginAt { get; set; }
}
