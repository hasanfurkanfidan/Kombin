using Kombin.Api.Persistence.Entities.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Kombin.Api.Features.Admin;

/// <summary>
/// Checks an admin's e-mail and password against <c>Admin:Users</c> with the Identity feature's PBKDF2 hasher (the same hash format the app's
/// users have, so one command makes both). An unknown e-mail costs the same time as a wrong password.
/// </summary>
internal sealed class AdminCredentials(IOptionsMonitor<AdminOptions> options, IPasswordHasher<User> hasher)
{
    private static readonly User Subject = new();

    private static readonly string DummyHash = new PasswordHasher<User>().HashPassword(Subject, "not-a-real-admin-password-1");

    /// <summary>The configured e-mail (as written in the config) when the pair is right, else null.</summary>
    public string? Verify(string? email, string? password)
    {
        var normalized = Normalize(email);
        var entry = options.CurrentValue.Users.FirstOrDefault(user => Normalize(user.Email) == normalized && !string.IsNullOrWhiteSpace(user.PasswordHash));
        var hash = entry?.PasswordHash ?? DummyHash;
        var result = Check(hash, password ?? string.Empty);

        return entry is not null && !string.IsNullOrEmpty(normalized) && result != PasswordVerificationResult.Failed ? entry.Email.Trim() : null;
    }

    /// <summary>A hash that is not Base64 (a mistyped config value) makes the framework hasher throw; it must fail the sign-in like a wrong password, not answer 500.</summary>
    private PasswordVerificationResult Check(string hash, string password)
    {
        try
        {
            return hasher.VerifyHashedPassword(Subject, hash, password);
        }
        catch (FormatException)
        {
            return PasswordVerificationResult.Failed;
        }
    }

    public static string Normalize(string? email) => email?.Trim().ToLowerInvariant() ?? string.Empty;
}
