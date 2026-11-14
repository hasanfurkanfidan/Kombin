using Kombin.Api.Common.Time;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Social;

namespace Kombin.Api.Features.Admin.Pages;

internal sealed class IndexModel(AdminDashboard dashboard, KombinDbContext db, IClock clock) : AdminPageModel
{
    public DashboardData Data { get; private set; } = null!;

    public int PendingReview { get; private set; }

    public DateTimeOffset Now => clock.UtcNow;

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Data = await dashboard.LoadAsync(cancellationToken);
        PendingReview = await db.Posts.CountAsync(post => post.Status == PostStatus.NeedsReview || post.Status == PostStatus.Hidden, cancellationToken);
    }
}
