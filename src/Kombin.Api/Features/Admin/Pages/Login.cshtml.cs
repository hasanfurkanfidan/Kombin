using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Kombin.Api.Features.Admin.Pages;

internal sealed partial class LoginModel(AdminCredentials credentials, AdminLoginThrottle throttle, AdminAuditLog audit, ILogger<LoginModel> logger) : AdminPageModel
{
    [BindProperty]
    public LoginInput Input { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public string? Error { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        var existing = await HttpContext.AuthenticateAsync(AdminAuth.Scheme);

        return existing.Succeeded ? Redirect(AdminAuth.PathPrefix) : Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        var client = AdminModule.ClientKey(HttpContext);

        if (throttle.IsLocked(client))
        {
            LogLocked(logger, client);
            Response.Headers.RetryAfter = ((int)AdminLoginThrottle.Window.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
            Error = "Too many failed attempts. Try again in 15 minutes.";

            return PageWithStatus(StatusCodes.Status429TooManyRequests);
        }

        var email = credentials.Verify(Input.Email, Input.Password);

        if (email is null)
        {
            throttle.RegisterFailure(client);
            LogFailed(logger, client);
            Error = "The e-mail or password is incorrect.";

            return PageWithStatus(StatusCodes.Status401Unauthorized);
        }

        throttle.Reset(client);
        await HttpContext.SignInAsync(
            AdminAuth.Scheme,
            AdminAuth.CreatePrincipal(email),
            new AuthenticationProperties { IsPersistent = false, AllowRefresh = true });
        await audit.RecordAsync(email, "admin.login", null, null, null, cancellationToken);

        return LocalRedirect(SafeReturnUrl(ReturnUrl));
    }

    /// <summary>Only a path inside the panel: an absolute URL or a protocol-relative one would be an open redirect.</summary>
    internal static string SafeReturnUrl(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl)
        && returnUrl.StartsWith(AdminAuth.PathPrefix, StringComparison.OrdinalIgnoreCase)
        && (returnUrl.Length == AdminAuth.PathPrefix.Length || returnUrl[AdminAuth.PathPrefix.Length] is '/' or '?')
        && !returnUrl.Contains("//", StringComparison.Ordinal)
        && !returnUrl.Contains('\\', StringComparison.Ordinal)
            ? returnUrl
            : AdminAuth.PathPrefix;

    private PageResult PageWithStatus(int statusCode)
    {
        Response.StatusCode = statusCode;

        return Page();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Admin sign-in failed from {Client}.")]
    private static partial void LogFailed(ILogger logger, string client);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Admin sign-in refused from {Client}: too many failures.")]
    private static partial void LogLocked(ILogger logger, string client);

    internal sealed class LoginInput
    {
        public string? Email { get; set; }

        public string? Password { get; set; }
    }
}
