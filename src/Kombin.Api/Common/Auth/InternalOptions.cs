using Microsoft.Extensions.Options;

namespace Kombin.Api.Common.Auth;

/// <summary>Settings for endpoints that only trusted internal workers (scrapers, jobs) may call.</summary>
internal sealed class InternalOptions
{
    public const string SectionName = "Internal";

    public string ApiKey { get; set; } = string.Empty;
}

internal sealed class InternalOptionsValidator(IHostEnvironment environment) : IValidateOptions<InternalOptions>
{
    internal const int MinimumApiKeyLength = 32;

    public ValidateOptionsResult Validate(string? name, InternalOptions options)
    {
        var failures = new List<string>();

        if (string.IsNullOrEmpty(options.ApiKey))
        {
            failures.Add("Internal:ApiKey is required.");
        }
        else
        {
            if (options.ApiKey.Length < MinimumApiKeyLength)
            {
                failures.Add($"Internal:ApiKey must be at least {MinimumApiKeyLength} characters long.");
            }

            if (IsPlaceholder(options.ApiKey) && !environment.IsDevelopment())
            {
                failures.Add("Internal:ApiKey is a known placeholder, which is only allowed in the Development environment.");
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    internal static bool IsPlaceholder(string apiKey) =>
        apiKey.StartsWith("dev-only-", StringComparison.OrdinalIgnoreCase)
        || apiKey.Contains("change-me", StringComparison.OrdinalIgnoreCase);
}
