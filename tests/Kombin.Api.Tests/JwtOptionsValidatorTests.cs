using Kombin.Api.Common.Auth;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Kombin.Api.Tests;

public class JwtOptionsValidatorTests
{
    private const string GoodKey = "production-grade-signing-key-9f8e7d6c5b4a";

    private static JwtOptions ValidOptions(string signingKey = GoodKey) => new()
    {
        Issuer = "issuer",
        Audience = "audience",
        SigningKey = signingKey,
    };

    private static JwtOptionsValidator CreateValidator(string environment) => new(new TestEnvironment(environment));

    [Fact]
    public void ValidConfigurationPasses()
    {
        var result = CreateValidator(Environments.Production).Validate(null, ValidOptions());

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void ShortSigningKeyFails()
    {
        var result = CreateValidator(Environments.Production).Validate(null, ValidOptions("short-key-1234567890"));

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, failure => failure.Contains("at least 32 bytes", StringComparison.Ordinal));
    }

    [Fact]
    public void LowEntropySigningKeyFails()
    {
        var result = CreateValidator(Environments.Production).Validate(null, ValidOptions(new string('a', 24) + new string('b', 24)));

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, failure => failure.Contains("distinct characters", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingSigningKeyFails()
    {
        var result = CreateValidator(Environments.Development).Validate(null, ValidOptions(string.Empty));

        Assert.True(result.Failed);
    }

    [Theory]
    [InlineData("dev-only-signing-key-change-me-0123456789abcdef")]
    [InlineData("Please-CHANGE-ME-before-deploying-0123456789")]
    public void PlaceholderKeyIsOnlyAllowedInDevelopment(string placeholder)
    {
        Assert.True(CreateValidator(Environments.Development).Validate(null, ValidOptions(placeholder)).Succeeded);

        var production = CreateValidator(Environments.Production).Validate(null, ValidOptions(placeholder));

        Assert.True(production.Failed);
        Assert.Contains(production.Failures!, failure => failure.Contains("placeholder", StringComparison.Ordinal));
    }

    [Fact]
    public void IssuerAndAudienceAreRequired()
    {
        var options = ValidOptions();
        options.Issuer = " ";
        options.Audience = string.Empty;

        var result = CreateValidator(Environments.Production).Validate(null, options);

        Assert.True(result.Failed);
        Assert.Equal(2, result.Failures!.Count());
    }

    [Fact]
    public void RefreshLifetimeMustOutlastAccessLifetime()
    {
        var options = ValidOptions();
        options.RefreshTokenLifetime = options.AccessTokenLifetime;

        var result = CreateValidator(Environments.Production).Validate(null, options);

        Assert.True(result.Failed);
    }

    private sealed class TestEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "Kombin.Api.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
