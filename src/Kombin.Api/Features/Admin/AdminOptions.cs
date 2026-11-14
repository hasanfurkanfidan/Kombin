using Microsoft.Extensions.Options;

namespace Kombin.Api.Features.Admin;

/// <summary>One founder who may sign in to <c>/admin</c>. <c>PasswordHash</c> is what <c>dotnet run --project src/Kombin.Api -- hash-password</c> prints.</summary>
internal sealed class AdminUserOptions
{
    public string Email { get; set; } = string.Empty;

    public string PasswordHash { get; set; } = string.Empty;
}

/// <summary>
/// <c>Admin:Users</c>: the admin panel's own accounts, separate from the app's users (and from its JWTs). An empty list is valid: nobody can
/// sign in, which is the safe default for an environment that does not need the panel.
/// </summary>
internal sealed class AdminOptions
{
    public const string SectionName = "Admin";

    public List<AdminUserOptions> Users { get; set; } = [];
}

internal sealed class AdminOptionsValidator : IValidateOptions<AdminOptions>
{
    public ValidateOptionsResult Validate(string? name, AdminOptions options)
    {
        var failures = new List<string>();

        // Compose maps ADMIN_EMAIL / ADMIN_PASSWORD_HASH to entry 0 even when they are blank; a blank entry is "not configured", not an error.
        var configured = options.Users.Where(user => !IsBlank(user)).ToList();

        foreach (var user in configured)
        {
            if (string.IsNullOrWhiteSpace(user.Email) || !user.Email.Contains('@', StringComparison.Ordinal))
            {
                failures.Add("Admin:Users: every entry needs an Email.");
            }

            if (string.IsNullOrWhiteSpace(user.PasswordHash))
            {
                failures.Add($"Admin:Users: '{user.Email}' has no PasswordHash (make one with `dotnet run --project src/Kombin.Api -- hash-password`).");
            }
        }

        var duplicate = configured.GroupBy(user => AdminCredentials.Normalize(user.Email)).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            failures.Add($"Admin:Users: '{duplicate.Key}' is listed twice.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static bool IsBlank(AdminUserOptions user) => string.IsNullOrWhiteSpace(user.Email) && string.IsNullOrWhiteSpace(user.PasswordHash);
}
