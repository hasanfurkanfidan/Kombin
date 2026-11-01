namespace Kombin.Api.Persistence.Entities.Identity;

internal sealed class RefreshToken : BaseEntity
{
    public Guid UserId { get; set; }

    /// <summary>SHA-256 (lowercase hex) of the opaque token. The raw token is never stored.</summary>
    public string TokenHash { get; set; } = string.Empty;

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    public Guid? ReplacedByTokenId { get; set; }

    public string? DeviceName { get; set; }

    /// <summary>Maps to PostgreSQL's <c>xmin</c> system column; guards against two parallel rotations of one token.</summary>
    public uint Version { get; set; }
}
