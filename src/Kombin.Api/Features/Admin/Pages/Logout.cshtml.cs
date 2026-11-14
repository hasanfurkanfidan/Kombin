using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;

namespace Kombin.Api.Features.Admin.Pages;

internal sealed class LogoutModel : AdminPageModel
{
    /// <summary>A GET (a bookmark, a prefetch) never signs anyone out; only the form's POST does.</summary>
    public IActionResult OnGet() => Redirect(AdminAuth.PathPrefix);

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        await AuditAsync("admin.logout", null, null, null, cancellationToken);
        await HttpContext.SignOutAsync(AdminAuth.Scheme);

        return Redirect(AdminAuth.LoginPath);
    }
}
