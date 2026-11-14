using System.Text;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Kombin.Api.Common.Hosting;

/// <summary>
/// The start-up check of the Production environment: the app refuses to start with a development default, a placeholder or a missing secret,
/// and lists every problem at once so the operator fixes them in one pass. Pure (reads configuration only), so it is unit-tested with
/// dev defaults and with generated values. It never prints a value, only the key and what is wrong with it.
/// </summary>
internal static class ProductionSettings
{
    internal const int MinimumSecretLength = 32;

    internal const int MinimumPasswordLength = 16;

    /// <summary>Passwords that ship in the repository, compose defaults or the usual image defaults.</summary>
    private static readonly string[] KnownDefaultPasswords =
        ["kombin_dev_password", "kombin_dev_minio_password", "kombin", "postgres", "password", "minioadmin", "changeme", "change-me"];

    public static IReadOnlyList<string> Validate(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var problems = new List<string>();

        CheckSecret(configuration, "Jwt:SigningKey", MinimumSecretLength, problems);
        CheckSecret(configuration, "Internal:ApiKey", MinimumSecretLength, problems);
        CheckSecret(configuration, "Vision:InternalKey", MinimumPasswordLength, problems);
        CheckSecret(configuration, "Outbound:IpSalt", MinimumPasswordLength, problems);
        CheckRequired(configuration, "Jwt:Issuer", problems);
        CheckRequired(configuration, "Jwt:Audience", problems);

        CheckDatabase(configuration, problems);
        CheckStorage(configuration, problems);
        CheckLegal(configuration, problems);
        CheckAdmin(configuration, problems);

        if (IsPlaceholder(configuration["CatalogEmbeddings:BotContact"] ?? "[YOUR EMAIL]"))
        {
            problems.Add("CatalogEmbeddings:BotContact is still a placeholder: it goes into the User-Agent that sellers see (set BOT_CONTACT).");
        }

        return problems;
    }

    /// <summary>The message printed when the app refuses to start.</summary>
    public static string Describe(IReadOnlyList<string> problems)
    {
        ArgumentNullException.ThrowIfNull(problems);

        var message = new StringBuilder();
        message.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"Refusing to start in Production: {problems.Count} configuration problem(s).");

        foreach (var problem in problems)
        {
            message.Append("  - ").AppendLine(problem);
        }

        message.Append("Generate strong values with scripts/gen-secrets.sh (or .ps1) and see docker/.env.prod.example.");

        return message.ToString();
    }

    /// <summary>A development value: the repository's "dev-only-" keys, anything saying "change me", or an empty "[...]" placeholder.</summary>
    internal static bool IsPlaceholder(string value) =>
        value.StartsWith("dev-only-", StringComparison.OrdinalIgnoreCase)
        || value.Contains("change-me", StringComparison.OrdinalIgnoreCase)
        || value.Contains("changeme", StringComparison.OrdinalIgnoreCase)
        || value.Contains("dev_minio", StringComparison.OrdinalIgnoreCase)
        || (value.StartsWith('[') && value.EndsWith(']'))
        || (value.StartsWith("{{", StringComparison.Ordinal) && value.EndsWith("}}", StringComparison.Ordinal));

    private static void CheckSecret(IConfiguration configuration, string key, int minimumLength, List<string> problems)
    {
        var value = configuration[key];

        if (string.IsNullOrWhiteSpace(value))
        {
            problems.Add($"{key} is not set.");
        }
        else if (IsPlaceholder(value))
        {
            problems.Add($"{key} is a development placeholder.");
        }
        else if (Encoding.UTF8.GetByteCount(value) < minimumLength)
        {
            problems.Add($"{key} must be at least {minimumLength} characters long.");
        }
    }

    private static void CheckRequired(IConfiguration configuration, string key, List<string> problems)
    {
        if (string.IsNullOrWhiteSpace(configuration[key]))
        {
            problems.Add($"{key} is not set.");
        }
    }

    private static void CheckDatabase(IConfiguration configuration, List<string> problems)
    {
        var connectionString = configuration.GetConnectionString("Postgres");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            problems.Add("ConnectionStrings:Postgres is not set.");
            return;
        }

        string? password;

        try
        {
            password = new NpgsqlConnectionStringBuilder(connectionString).Password;
        }
        catch (ArgumentException)
        {
            problems.Add("ConnectionStrings:Postgres is not a valid connection string.");
            return;
        }

        if (string.IsNullOrEmpty(password))
        {
            problems.Add("The database password (in ConnectionStrings:Postgres) is empty.");
        }
        else if (IsDefaultPassword(password))
        {
            problems.Add("The database password (in ConnectionStrings:Postgres) is a default or placeholder (set POSTGRES_PASSWORD).");
        }
        else if (password.Length < MinimumPasswordLength)
        {
            problems.Add($"The database password (in ConnectionStrings:Postgres) must be at least {MinimumPasswordLength} characters long.");
        }
    }

    private static void CheckStorage(IConfiguration configuration, List<string> problems)
    {
        CheckRequired(configuration, "Storage:Endpoint", problems);
        CheckRequired(configuration, "Storage:Bucket", problems);
        CheckRequired(configuration, "Storage:AccessKey", problems);

        var secret = configuration["Storage:SecretKey"];

        if (string.IsNullOrWhiteSpace(secret))
        {
            problems.Add("Storage:SecretKey is not set.");
        }
        else if (IsDefaultPassword(secret))
        {
            problems.Add("Storage:SecretKey is a default or placeholder (set MINIO_ROOT_PASSWORD, or the R2/S3 secret).");
        }
        else if (secret.Length < MinimumPasswordLength)
        {
            problems.Add($"Storage:SecretKey must be at least {MinimumPasswordLength} characters long.");
        }

        var publicBase = configuration["Storage:PublicBaseUrl"];

        if (string.IsNullOrWhiteSpace(publicBase))
        {
            problems.Add("Storage:PublicBaseUrl is not set (the app loads public images from it).");
        }
        else if (!Uri.TryCreate(publicBase, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.IsLoopback)
        {
            problems.Add("Storage:PublicBaseUrl must be an https address the phones can reach (not localhost).");
        }
    }

    private static void CheckLegal(IConfiguration configuration, List<string> problems)
    {
        foreach (var key in new[] { "Legal:ControllerName", "Legal:ContactEmail", "Legal:Address", "Legal:Version" })
        {
            var value = configuration[key];

            if (string.IsNullOrWhiteSpace(value))
            {
                problems.Add($"{key} is not set (the privacy policy needs the data controller's details).");
            }
            else if (IsPlaceholder(value))
            {
                problems.Add($"{key} is still a placeholder.");
            }
        }

        var email = configuration["Legal:ContactEmail"];

        if (!string.IsNullOrWhiteSpace(email) && !IsPlaceholder(email) && !email.Contains('@', StringComparison.Ordinal))
        {
            problems.Add("Legal:ContactEmail is not an e-mail address.");
        }
    }

    private static void CheckAdmin(IConfiguration configuration, List<string> problems)
    {
        var complete = configuration.GetSection("Admin:Users").GetChildren()
            .Count(user => !string.IsNullOrWhiteSpace(user["Email"]) && !string.IsNullOrWhiteSpace(user["PasswordHash"]));

        if (complete == 0)
        {
            problems.Add("Admin:Users is empty: nobody could review posts or disable accounts (set ADMIN_EMAIL and ADMIN_PASSWORD_HASH).");
        }
    }

    private static bool IsDefaultPassword(string value) =>
        IsPlaceholder(value) || KnownDefaultPasswords.Contains(value, StringComparer.OrdinalIgnoreCase);
}

/// <summary>Marker for the options machinery: validating it is the Production settings check (<see cref="ProductionSettingsValidator"/>).</summary>
internal sealed class ProductionSettingsCheck;

/// <summary>Runs <see cref="ProductionSettings"/> in the Production environment and does nothing elsewhere.</summary>
internal sealed class ProductionSettingsValidator(IConfiguration configuration, IHostEnvironment environment) : IValidateOptions<ProductionSettingsCheck>
{
    public ValidateOptionsResult Validate(string? name, ProductionSettingsCheck options)
    {
        if (!environment.IsProduction())
        {
            return ValidateOptionsResult.Success;
        }

        var problems = ProductionSettings.Validate(configuration);

        return problems.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(ProductionSettings.Describe(problems));
    }
}
