namespace Kombin.Api.Features.Admin.Pages;

/// <summary><c>/admin/system</c>, "Sistem durumu": errors, failed jobs, vision, league closes, workers, Sentry. Read-only.</summary>
internal sealed class SystemModel(SystemStatus status) : AdminPageModel
{
    public SystemStatusData Data { get; private set; } = null!;

    public async Task OnGetAsync(CancellationToken cancellationToken) => Data = await status.LoadAsync(cancellationToken);
}
