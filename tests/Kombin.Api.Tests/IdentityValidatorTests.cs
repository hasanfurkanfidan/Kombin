using Kombin.Api.Features.Identity.Login;
using Kombin.Api.Features.Identity.Register;

namespace Kombin.Api.Tests;

public class IdentityValidatorTests
{
    private readonly RegisterValidator _register = new();
    private readonly LoginValidator _login = new();

    private static RegisterRequest ValidRegistration() => new("ada@example.com", "Passw0rdOk", "Ada Lovelace");

    [Theory]
    [InlineData(null, true)]
    [InlineData("0b1f6c3e-5f7a-4b19-9d57-0d3a8f3a9c11", true)]
    [InlineData("", false)]
    [InlineData("has space", false)]
    public void TheAnonymousIdIsOptionalOnRegistrationAndLogin(string? anonymousId, bool valid)
    {
        Assert.Equal(valid, _register.Validate(ValidRegistration() with { AnonymousId = anonymousId }).IsValid);
        Assert.Equal(valid, _login.Validate(new LoginRequest("ada@example.com", "Passw0rdOk", null, anonymousId)).IsValid);
    }

    [Fact]
    public void TheAnonymousIdIsAtMost64CharactersOnRegistrationAndLogin()
    {
        var tooLong = new string('a', 65);

        Assert.False(_register.Validate(ValidRegistration() with { AnonymousId = tooLong }).IsValid);
        Assert.False(_login.Validate(new LoginRequest("ada@example.com", "Passw0rdOk", null, tooLong)).IsValid);
    }

    [Fact]
    public void ValidRegistrationPasses()
    {
        Assert.True(_register.Validate(ValidRegistration()).IsValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-email")]
    [InlineData("missing-domain@")]
    public void RegistrationRejectsBadEmail(string email)
    {
        var result = _register.Validate(ValidRegistration() with { Email = email });

        Assert.Contains(result.Errors, failure => failure.PropertyName == nameof(RegisterRequest.Email));
    }

    [Fact]
    public void RegistrationRejectsPasswordShorterThanEightCharacters()
    {
        var result = _register.Validate(ValidRegistration() with { Password = "short1" });

        // Matched by error code: FluentValidation's built-in message text depends on the culture of the machine.
        Assert.Contains(
            result.Errors,
            failure => failure.PropertyName == nameof(RegisterRequest.Password) && failure.ErrorCode == "MinimumLengthValidator");
    }

    [Theory]
    [InlineData("lettersonly", "digit")]
    [InlineData("12345678", "letter")]
    public void RegistrationRequiresALetterAndADigit(string password, string expectedMessageFragment)
    {
        var result = _register.Validate(ValidRegistration() with { Password = password });

        Assert.Contains(
            result.Errors,
            failure => failure.PropertyName == nameof(RegisterRequest.Password)
                && failure.ErrorMessage.Contains(expectedMessageFragment, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("A")]
    [InlineData(" A ")]
    [InlineData("")]
    public void RegistrationRejectsTooShortDisplayName(string displayName)
    {
        var result = _register.Validate(ValidRegistration() with { DisplayName = displayName });

        Assert.Contains(result.Errors, failure => failure.PropertyName == nameof(RegisterRequest.DisplayName));
    }

    [Fact]
    public void RegistrationRejectsTooLongDisplayName()
    {
        var result = _register.Validate(ValidRegistration() with { DisplayName = new string('x', 51) });

        Assert.Contains(result.Errors, failure => failure.PropertyName == nameof(RegisterRequest.DisplayName));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(50)]
    public void RegistrationAcceptsDisplayNameAtTheBoundaries(int length)
    {
        var result = _register.Validate(ValidRegistration() with { DisplayName = new string('x', length) });

        Assert.True(result.IsValid);
    }

    [Fact]
    public void ValidLoginPasses()
    {
        Assert.True(_login.Validate(new LoginRequest("ada@example.com", "whatever", "Pixel 9")).IsValid);
    }

    [Fact]
    public void LoginDoesNotApplyPasswordComplexityRules()
    {
        Assert.True(_login.Validate(new LoginRequest("ada@example.com", "x")).IsValid);
    }

    [Fact]
    public void LoginRequiresEmailAndPassword()
    {
        var result = _login.Validate(new LoginRequest("not-an-email", string.Empty));

        Assert.Contains(result.Errors, failure => failure.PropertyName == nameof(LoginRequest.Email));
        Assert.Contains(result.Errors, failure => failure.PropertyName == nameof(LoginRequest.Password));
    }

    [Fact]
    public void LoginRejectsOverlongDeviceName()
    {
        var result = _login.Validate(new LoginRequest("ada@example.com", "secret", new string('d', 101)));

        Assert.Contains(result.Errors, failure => failure.PropertyName == nameof(LoginRequest.DeviceName));
    }
}
