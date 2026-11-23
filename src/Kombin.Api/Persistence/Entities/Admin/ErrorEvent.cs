namespace Kombin.Api.Persistence.Entities.Admin;

/// <summary>
/// One unhandled error, counted for <c>/admin/system</c> (<c>ErrorLog</c>, Admin rules). Counts and codes only: no user, no path, no message.
/// <c>CreatedAt</c> is when it happened; rows older than 30 days are deleted by <c>ErrorLog</c>.
/// </summary>
internal sealed class ErrorEvent : BaseEntity
{
    public const int MaxKindLength = 16;

    public const int MaxSourceLength = 200;

    public const int MaxErrorCodeLength = 200;

    /// <summary><c>request</c> or <c>job</c>.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>The route template (<c>/api/v1/wardrobe/items/{id}</c>) or the job's name (<c>WardrobeAnalyzeJob.RunAsync</c>).</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>The exception's type name; never its message.</summary>
    public string ErrorCode { get; set; } = string.Empty;
}
