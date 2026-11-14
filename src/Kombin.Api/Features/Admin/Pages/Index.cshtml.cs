using Kombin.Api.Common.Time;
using Kombin.Api.Features.Social;
using Kombin.Api.Persistence;

namespace Kombin.Api.Features.Admin.Pages;

internal sealed class IndexModel(AdminDashboard dashboard, KombinDbContext db, IClock clock) : AdminPageModel
{
    public DashboardData Data { get; private set; } = null!;

    public int PendingReview { get; private set; }

    public DateTimeOffset Now => clock.UtcNow;

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Data = await dashboard.LoadAsync(cancellationToken);
        PendingReview = await db.Posts.CountAsync(ReportRules.InReviewQueue, cancellationToken);
    }
}
