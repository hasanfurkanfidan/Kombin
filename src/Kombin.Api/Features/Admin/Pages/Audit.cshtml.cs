using Kombin.Api.Common.Pagination;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Admin;

namespace Kombin.Api.Features.Admin.Pages;

internal sealed class AuditModel(KombinDbContext db) : AdminPageModel
{
    public const int PageSize = 50;

    public PagedResponse<AdminAuditEntry> Entries { get; private set; } = null!;

    /// <summary>The page number is <c>p</c>: <c>page</c> is reserved by Razor Pages (it is the page's own route value).</summary>
    public async Task OnGetAsync(int? p, CancellationToken cancellationToken)
    {
        var request = new PagedRequest(p ?? 1, PageSize);
        var total = await db.AdminAuditLog.CountAsync(cancellationToken);
        var items = await db.AdminAuditLog.AsNoTracking()
            .OrderByDescending(entry => entry.CreatedAt)
            .ThenByDescending(entry => entry.Id)
            .Skip(request.Skip)
            .Take(request.EffectivePageSize)
            .ToListAsync(cancellationToken);

        Entries = PagedResponse<AdminAuditEntry>.Create(items, request, total);
    }
}
