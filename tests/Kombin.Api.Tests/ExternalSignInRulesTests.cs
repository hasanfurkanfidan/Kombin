using Kombin.Api.Features.Identity;
using Kombin.Api.Features.Identity.ExternalSignIn;
using Kombin.Api.Features.Identity.ExternalTokens;
using Kombin.Api.Persistence.Entities.Identity;

namespace Kombin.Api.Tests;

public class ExternalSignInRulesTests
{
    private static ExternalIdentity Identity(string? email = "ada@example.com", bool verified = true, string? name = null) =>
        new(ExternalProviders.Google, "google-sub-1", email, verified, name);

    [Fact]
    public void AnExistingLinkSignsThatUserIn()
    {
        var step = ExternalSignInRules.Decide(Identity(), linkExists: true, userWithEmailExists: true);

        Assert.Equal(ExternalSignInStep.SignInLinked, step.Value);
    }

    [Fact]
    public void AnExistingLinkWinsEvenWithoutAnEmailInTheToken()
    {
        // Apple may leave the email out of later tokens; the subject alone identifies a linked account.
        var step = ExternalSignInRules.Decide(Identity(email: null, verified: false), linkExists: true, userWithEmailExists: false);

        Assert.Equal(ExternalSignInStep.SignInLinked, step.Value);
    }

    [Fact]
    public void AnUnlinkedTokenWhoseEmailHasAnAccountLinksToIt()
    {
        var step = ExternalSignInRules.Decide(Identity(), linkExists: false, userWithEmailExists: true);

        Assert.Equal(ExternalSignInStep.LinkByEmail, step.Value);
    }

    [Fact]
    public void AnUnknownTokenCreatesAUser()
    {
        var step = ExternalSignInRules.Decide(Identity(), linkExists: false, userWithEmailExists: false);

        Assert.Equal(ExternalSignInStep.CreateUser, step.Value);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("ada@example.com", false)]
    public void WithoutAVerifiedEmailNothingIsLinkedOrCreated(string? email, bool verified)
    {
        var withAccount = ExternalSignInRules.Decide(Identity(email, verified), linkExists: false, userWithEmailExists: true);
        var withoutAccount = ExternalSignInRules.Decide(Identity(email, verified), linkExists: false, userWithEmailExists: false);

        Assert.Equal(IdentityErrors.ExternalEmailMissing, withAccount.Error);
        Assert.Equal(IdentityErrors.ExternalEmailMissing, withoutAccount.Error);
    }

    [Fact]
    public void TheRequestedDisplayNameComesFirst() =>
        Assert.Equal("Ada L.", ExternalSignInRules.ChooseDisplayName("  Ada L.  ", "Ada Lovelace", "ada@example.com"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TheTokenNameIsUsedWhenTheRequestHasNone(string? requested) =>
        Assert.Equal("Ada Lovelace", ExternalSignInRules.ChooseDisplayName(requested, " Ada Lovelace ", "ada@example.com"));

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    public void TheEmailLocalPartIsTheLastResort(string? tokenName) =>
        Assert.Equal("x7k2m9p4qd", ExternalSignInRules.ChooseDisplayName(null, tokenName, "x7k2m9p4qd@privaterelay.appleid.com"));

    [Fact]
    public void ALongNameIsCutToFiftyCharacters()
    {
        var name = ExternalSignInRules.ChooseDisplayName(null, new string('a', 80), "ada@example.com");

        Assert.Equal(new string('a', 50), name);
    }

    [Fact]
    public void CuttingNeverSplitsAnEmoji()
    {
        // 49 letters then a two-char emoji: keeping it would make 51 characters, splitting it would leave half a surrogate pair.
        var name = ExternalSignInRules.ChooseDisplayName(null, new string('a', 49) + "\U0001F600" + "bbb", "ada@example.com");

        Assert.Equal(new string('a', 49), name);
    }
}
