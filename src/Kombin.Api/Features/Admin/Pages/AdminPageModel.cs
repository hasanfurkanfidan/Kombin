using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Kombin.Api.Features.Admin.Pages;

/// <summary>What every admin page shares: who is signed in, the flash message that survives a redirect, and the audit trail.</summary>
internal abstract class AdminPageModel : PageModel
{
    private const string FlashKey = "flash";

    private const string FlashErrorKey = "flashError";

    public string? Flash => TempData[FlashKey] as string;

    public string? FlashError => TempData[FlashErrorKey] as string;

    /// <summary>The admin's e-mail from <c>Admin:Users</c>.</summary>
    protected string Actor => User.Identity?.Name ?? "unknown";

    internal AdminAuditLog AuditLog => HttpContext.RequestServices.GetRequiredService<AdminAuditLog>();

    protected void SetFlash(string message) => TempData[FlashKey] = message;

    protected void SetFlashError(string message) => TempData[FlashErrorKey] = message;

    internal Task AuditAsync(string action, string? targetType, string? targetId, string? summary, CancellationToken cancellationToken) =>
        AuditLog.RecordAsync(Actor, action, targetType, targetId, summary, cancellationToken);
}
