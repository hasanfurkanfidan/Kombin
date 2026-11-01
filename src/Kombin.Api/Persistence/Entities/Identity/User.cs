namespace Kombin.Api.Persistence.Entities.Identity;

internal sealed class User : BaseEntity
{
    /// <summary>Always stored trimmed and lower-cased so the unique index is case-insensitive.</summary>
    public string Email { get; set; } = string.Empty;

    public string PasswordHash { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public DateTimeOffset? LastLoginAt { get; set; }
}
