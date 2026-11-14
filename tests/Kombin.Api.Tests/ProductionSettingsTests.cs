using Kombin.Api.Common.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Kombin.Api.Tests;

public class ProductionSettingsTests
{
    /// <summary>What <c>scripts/gen-secrets</c> and a filled-in <c>.env.prod</c> amount to.</summary>
    private static Dictionary<string, string?> Generated() => new()
    {
        ["ConnectionStrings:Postgres"] = "Host=db;Port=5432;Database=kombin;Username=kombin;Password=9f3c1a7e5b2d4f60a8c1e3b5d7f90214",
        ["Jwt:Issuer"] = "kombin-api",
        ["Jwt:Audience"] = "kombin-app",
        ["Jwt:SigningKey"] = "vZq3k0m8s2Yt1R7wXn5LpC9aHdE4uJfBgT6oIiKxM0yQ1eNrWz3SbVcUhD8lAjPk",
        ["Internal:ApiKey"] = "Qm7Xc2LpT9vBn4RkHs8WjYd1AeUf6ZgIo3NtCxM5qVrPwKb0",
        ["Vision:InternalKey"] = "r8Ks2Vn5Xc9LpTq4BmHj7WdYe1AfUg6Z",
        ["Outbound:IpSalt"] = "N4tCxM5qVrPwKb0Qm7Xc2LpT9vBn8RkH",
        ["Storage:Endpoint"] = "http://minio:9000",
        ["Storage:Bucket"] = "kombin",
        ["Storage:AccessKey"] = "kombin-app",
        ["Storage:SecretKey"] = "d1AeUf6ZgIo3NtCxM5qVrPwKb0Qm7Xc2",
        ["Storage:PublicBaseUrl"] = "https://storage.kombin.app/kombin",
        ["Legal:Version"] = "1.0",
        ["Legal:ControllerName"] = "Kombin Teknoloji A.Ş.",
        ["Legal:ContactEmail"] = "gizlilik@kombin.app",
        ["Legal:Address"] = "Maslak Mah. Büyükdere Cad. No:1 Sarıyer / İstanbul",
        ["Admin:Users:0:Email"] = "founder@kombin.app",
        ["Admin:Users:0:PasswordHash"] = "AQAAAAIAAYagAAAAEExampleHashExampleHashExampleHash==",
        ["CatalogEmbeddings:BotContact"] = "bot@kombin.app",
    };

    private static IConfiguration Config(Dictionary<string, string?> values) => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static IReadOnlyList<string> Problems(Action<Dictionary<string, string?>>? change = null)
    {
        var values = Generated();
        change?.Invoke(values);

        return ProductionSettings.Validate(Config(values));
    }

    [Fact]
    public void GeneratedSettingsPass()
    {
        Assert.Empty(Problems());
    }

    [Fact]
    public void TheDevelopmentDefaultsAreAllRefusedAndListedTogether()
    {
        var problems = Problems(values =>
        {
            values["Jwt:SigningKey"] = "dev-only-signing-key-change-me-0123456789abcdef";
            values["Internal:ApiKey"] = "dev-only-internal-key-change-me-0123456789abcdef";
            values["Outbound:IpSalt"] = "dev-only-ip-salt-change-me-0123456789abcdef";
            values["Vision:InternalKey"] = "";
            values["ConnectionStrings:Postgres"] = "Host=localhost;Port=5432;Database=kombin;Username=kombin;Password=kombin_dev_password";
            values["Storage:SecretKey"] = "kombin_dev_minio_password";
            values["Storage:PublicBaseUrl"] = "http://localhost:9000/kombin";
            values["Legal:ControllerName"] = "";
            values["Legal:ContactEmail"] = "{{CONTACT_EMAIL}}";
            values.Remove("Legal:Address");
            values["Admin:Users:0:Email"] = "";
            values["CatalogEmbeddings:BotContact"] = "[YOUR EMAIL]";
        });

        foreach (var key in new[]
        {
            "Jwt:SigningKey", "Internal:ApiKey", "Outbound:IpSalt", "Vision:InternalKey", "database password", "Storage:SecretKey",
            "Storage:PublicBaseUrl", "Legal:ControllerName", "Legal:ContactEmail", "Legal:Address", "Admin:Users", "CatalogEmbeddings:BotContact",
        })
        {
            Assert.Contains(problems, problem => problem.Contains(key, StringComparison.Ordinal));
        }

        Assert.Equal(12, problems.Count);
    }

    [Theory]
    [InlineData("short-but-random-1", false)]
    [InlineData("0123456789abcdef0123456789abcde", false)]
    [InlineData("0123456789abcdef0123456789abcdef", true)]
    public void TheSigningKeyAndTheInternalKeyNeedThirtyTwoBytes(string key, bool valid)
    {
        Assert.Equal(valid, !Problems(values => values["Jwt:SigningKey"] = key).Any(problem => problem.Contains("Jwt:SigningKey", StringComparison.Ordinal)));
        Assert.Equal(valid, !Problems(values => values["Internal:ApiKey"] = key).Any(problem => problem.Contains("Internal:ApiKey", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("kombin_dev_password")]
    [InlineData("postgres")]
    [InlineData("change-me-please-1234")]
    [InlineData("short")]
    [InlineData("")]
    public void TheDatabasePasswordMustNotBeADefaultOrShort(string password)
    {
        var problems = Problems(values => values["ConnectionStrings:Postgres"] = $"Host=db;Database=kombin;Username=kombin;Password={password}");

        Assert.Contains(problems, problem => problem.Contains("database password", StringComparison.Ordinal));
    }

    [Fact]
    public void ABrokenOrMissingConnectionStringIsAProblemToo()
    {
        Assert.Contains(Problems(values => values.Remove("ConnectionStrings:Postgres")), problem => problem.Contains("ConnectionStrings:Postgres is not set", StringComparison.Ordinal));
        Assert.Contains(Problems(values => values["ConnectionStrings:Postgres"] = "nonsense"), problem => problem.Contains("not a valid connection string", StringComparison.Ordinal));
    }

    [Fact]
    public void TheAdminListNeedsOneCompleteEntry()
    {
        Assert.Contains(Problems(values => values["Admin:Users:0:PasswordHash"] = ""), problem => problem.Contains("Admin:Users", StringComparison.Ordinal));
        Assert.DoesNotContain(
            Problems(values =>
            {
                // Compose maps a blank entry 0 even when the founder is entry 1; one complete entry is enough.
                values["Admin:Users:1:Email"] = values["Admin:Users:0:Email"];
                values["Admin:Users:1:PasswordHash"] = values["Admin:Users:0:PasswordHash"];
                values["Admin:Users:0:Email"] = "";
                values["Admin:Users:0:PasswordHash"] = "";
            }),
            problem => problem.Contains("Admin:Users", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("http://storage.kombin.app/kombin")]
    [InlineData("https://localhost:9000/kombin")]
    [InlineData("not a url")]
    public void ThePublicStorageAddressMustBeAnHttpsAddressThePhonesCanReach(string address)
    {
        Assert.Contains(Problems(values => values["Storage:PublicBaseUrl"] = address), problem => problem.Contains("Storage:PublicBaseUrl", StringComparison.Ordinal));
    }

    [Fact]
    public void TheLegalEmailMustLookLikeOne()
    {
        Assert.Contains(Problems(values => values["Legal:ContactEmail"] = "privacy"), problem => problem.Contains("not an e-mail", StringComparison.Ordinal));
    }

    [Fact]
    public void TheMessageNeverContainsAValue()
    {
        var problems = Problems(values =>
        {
            values["Jwt:SigningKey"] = "dev-only-signing-key-change-me-0123456789abcdef";
            values["ConnectionStrings:Postgres"] = "Host=db;Database=kombin;Username=kombin;Password=kombin_dev_password";
        });

        var message = ProductionSettings.Describe(problems);

        Assert.StartsWith("Refusing to start in Production: 2 configuration problem(s).", message, StringComparison.Ordinal);
        Assert.Contains("  - Jwt:SigningKey", message, StringComparison.Ordinal);
        Assert.DoesNotContain("0123456789abcdef", message, StringComparison.Ordinal);
        Assert.DoesNotContain("kombin_dev_password", message, StringComparison.Ordinal);
        Assert.Contains("gen-secrets", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Production", false)]
    [InlineData("Development", true)]
    [InlineData("Staging", true)]
    public void OnlyTheProductionEnvironmentIsChecked(string environment, bool passes)
    {
        var validator = new ProductionSettingsValidator(Config([]), new FakeEnvironment(environment));

        // An empty configuration is full of problems: it must only matter in Production.
        Assert.Equal(passes, validator.Validate(null, new ProductionSettingsCheck()).Succeeded);
    }

    [Theory]
    [InlineData("dev-only-anything", true)]
    [InlineData("Please-CHANGE-ME-now", true)]
    [InlineData("changeme", true)]
    [InlineData("kombin_dev_minio_password", true)]
    [InlineData("[YOUR EMAIL]", true)]
    [InlineData("{{ADDRESS}}", true)]
    [InlineData("vZq3k0m8s2Yt1R7wXn5L", false)]
    public void PlaceholdersAreRecognised(string value, bool placeholder)
    {
        Assert.Equal(placeholder, ProductionSettings.IsPlaceholder(value));
    }

    private sealed class FakeEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "Kombin.Api";

        public string ContentRootPath { get; set; } = string.Empty;

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
