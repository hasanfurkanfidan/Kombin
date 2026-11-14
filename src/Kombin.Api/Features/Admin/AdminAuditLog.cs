using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Admin;

namespace Kombin.Api.Features.Admin;

/// <summary>Writes <c>admin.audit_log</c>. Every admin action calls <see cref="RecordAsync"/> once it succeeded.</summary>
internal sealed class AdminAuditLog(KombinDbContext db)
{
    public async Task RecordAsync(string actor, string action, string? targetType, string? targetId, string? summary, CancellationToken cancellationToken)
    {
        db.AdminAuditLog.Add(new AdminAuditEntry
        {
            Actor = Cut(actor, AdminAuditEntry.MaxActorLength)!,
            Action = Cut(action, AdminAuditEntry.MaxActionLength)!,
            TargetType = Cut(targetType, AdminAuditEntry.MaxTargetTypeLength),
            TargetId = Cut(targetId, AdminAuditEntry.MaxTargetIdLength),
            Summary = Cut(summary, AdminAuditEntry.MaxSummaryLength),
        });

        await db.SaveChangesAsync(cancellationToken);
    }

    private static string? Cut(string? value, int maxLength) =>
        value is null || value.Length <= maxLength ? value : value[..maxLength];
}
