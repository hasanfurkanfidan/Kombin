using Microsoft.Extensions.Options;

namespace Kombin.Api.Features.Engagement;

/// <summary>Settings of the <c>/go/{offerId}</c> redirect.</summary>
internal sealed class OutboundOptions
{
    public const string SectionName = "Outbound";

    /// <summary>Mixed into the SHA-256 of a caller's IP address so stored hashes cannot be reversed with a table of all IPs.</summary>
    public string IpSalt { get; set; } = string.Empty;
}

internal sealed class OutboundOptionsValidator(IHostEnvironment environment) : IValidateOptions<OutboundOptions>
{
    internal const int MinimumSaltLength = 16;

    public ValidateOptionsResult Validate(string? name, OutboundOptions options)
    {
        var failures = new List<string>();

        if (string.IsNullOrEmpty(options.IpSalt))
        {
            failures.Add("Outbound:IpSalt is required.");
        }
        else
        {
            if (options.IpSalt.Length < MinimumSaltLength)
            {
                failures.Add($"Outbound:IpSalt must be at least {MinimumSaltLength} characters long.");
            }

            if (IsPlaceholder(options.IpSalt) && !environment.IsDevelopment())
            {
                failures.Add("Outbound:IpSalt is a known placeholder, which is only allowed in the Development environment.");
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    internal static bool IsPlaceholder(string salt) =>
        salt.StartsWith("dev-only-", StringComparison.OrdinalIgnoreCase)
        || salt.Contains("change-me", StringComparison.OrdinalIgnoreCase);
}
