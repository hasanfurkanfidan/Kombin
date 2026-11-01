using Microsoft.Extensions.Options;

namespace Kombin.Api.Common.Auth;

internal sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Issuer { get; set; } = string.Empty;

    public string Audience { get; set; } = string.Empty;

    public string SigningKey { get; set; } = string.Empty;

    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromMinutes(15);

    public TimeSpan RefreshTokenLifetime { get; set; } = TimeSpan.FromDays(30);
}

internal sealed class JwtOptionsValidator(IHostEnvironment environment) : IValidateOptions<JwtOptions>
{
    internal const int MinimumSigningKeyBytes = 32;

    internal const int MinimumDistinctCharacters = 8;

    public ValidateOptionsResult Validate(string? name, JwtOptions options)
    {
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.Issuer))
        {
            failures.Add("Jwt:Issuer is required.");
        }

        if (string.IsNullOrWhiteSpace(options.Audience))
        {
            failures.Add("Jwt:Audience is required.");
        }

        if (options.AccessTokenLifetime <= TimeSpan.Zero)
        {
            failures.Add("Jwt:AccessTokenLifetime must be positive.");
        }

        if (options.RefreshTokenLifetime <= options.AccessTokenLifetime)
        {
            failures.Add("Jwt:RefreshTokenLifetime must be longer than Jwt:AccessTokenLifetime.");
        }

        ValidateSigningKey(options.SigningKey, failures);

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    internal static bool IsPlaceholder(string signingKey) =>
        signingKey.StartsWith("dev-only-", StringComparison.OrdinalIgnoreCase)
        || signingKey.Contains("change-me", StringComparison.OrdinalIgnoreCase);

    private void ValidateSigningKey(string signingKey, List<string> failures)
    {
        if (string.IsNullOrEmpty(signingKey))
        {
            failures.Add("Jwt:SigningKey is required.");
            return;
        }

        if (System.Text.Encoding.UTF8.GetByteCount(signingKey) < MinimumSigningKeyBytes)
        {
            failures.Add($"Jwt:SigningKey must be at least {MinimumSigningKeyBytes} bytes long.");
        }

        if (signingKey.Distinct().Count() < MinimumDistinctCharacters)
        {
            failures.Add($"Jwt:SigningKey must contain at least {MinimumDistinctCharacters} distinct characters.");
        }

        if (IsPlaceholder(signingKey) && !environment.IsDevelopment())
        {
            failures.Add("Jwt:SigningKey is a known placeholder, which is only allowed in the Development environment.");
        }
    }
}
