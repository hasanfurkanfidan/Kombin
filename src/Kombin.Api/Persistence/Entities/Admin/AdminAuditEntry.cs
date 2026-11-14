namespace Kombin.Api.Persistence.Entities.Admin;

/// <summary>
/// One thing a founder did in the admin panel. Append-only. <see cref="Summary"/> is short and holds no personal data (IDs, handles, enum
/// names and the removal reason the author was shown, never e-mails); <c>CreatedAt</c> is when it happened.
/// </summary>
internal sealed class AdminAuditEntry : BaseEntity
{
    public const int MaxActorLength = 320;

    public const int MaxActionLength = 64;

    public const int MaxTargetTypeLength = 32;

    public const int MaxTargetIdLength = 64;

    public const int MaxSummaryLength = 500;

    /// <summary>The admin's e-mail from <c>Admin:Users</c> (not an app user).</summary>
    public string Actor { get; set; } = string.Empty;

    /// <summary>A dotted name such as <c>post.approve</c> or <c>creator.invite</c>.</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary><c>post</c>, <c>user</c>, <c>creator</c>, <c>catalog</c>, ... or null for an action without a target.</summary>
    public string? TargetType { get; set; }

    public string? TargetId { get; set; }

    public string? Summary { get; set; }
}
