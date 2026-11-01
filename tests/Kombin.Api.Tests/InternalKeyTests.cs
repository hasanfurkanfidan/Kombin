using Kombin.Api.Common.Auth;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Kombin.Api.Tests;

public class InternalKeyTests
{
    private const string ValidKey = "a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8";

    [Fact]
    public void MatchingKeyIsAccepted()
    {
        Assert.True(InternalKeyFilter.IsValid(ValidKey, ValidKey));
    }

    [Theory]
    [InlineData("")]
    [InlineData("a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f9")]
    [InlineData("a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f")]
    public void MissingOrDifferentKeyIsRejected(string provided)
    {
        Assert.False(InternalKeyFilter.IsValid(provided, ValidKey));
    }

    [Fact]
    public void UnconfiguredKeyNeverMatches()
    {
        Assert.False(InternalKeyFilter.IsValid(string.Empty, string.Empty));
    }

    private static ValidateOptionsResult Validate(string apiKey, string environment) =>
        new InternalOptionsValidator(new StubEnvironment(environment)).Validate(null, new InternalOptions { ApiKey = apiKey });

    [Fact]
    public void StrongKeyIsValidEverywhere()
    {
        Assert.True(Validate(ValidKey, Environments.Production).Succeeded);
    }

    [Fact]
    public void MissingKeyFails()
    {
        Assert.True(Validate(string.Empty, Environments.Development).Failed);
    }

    [Fact]
    public void KeyShorterThan32CharactersFails()
    {
        Assert.True(Validate(new string('x', 31), Environments.Development).Failed);
        Assert.True(Validate(new string('x', 32), Environments.Development).Succeeded);
    }

    [Fact]
    public void PlaceholderIsOnlyAllowedInDevelopment()
    {
        const string placeholder = "dev-only-internal-key-change-me-0123456789abcdef";

        Assert.True(Validate(placeholder, Environments.Development).Succeeded);
        Assert.True(Validate(placeholder, Environments.Production).Failed);
    }

    private sealed class StubEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "tests";

        public string ContentRootPath { get; set; } = string.Empty;

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
