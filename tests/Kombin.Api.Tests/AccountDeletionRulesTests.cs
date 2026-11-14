using Kombin.Api.Features.Identity;
using Kombin.Api.Features.Identity.CancelDeletion;
using Kombin.Api.Features.Identity.DeleteAccount;
using Kombin.Api.Features.Social;
using Kombin.Api.Persistence.Entities.Social;

namespace Kombin.Api.Tests;

public class AccountDeletionRulesTests
{
    private static readonly DateTimeOffset Requested = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TheWindowIsSevenDays()
    {
        Assert.Equal(TimeSpan.FromDays(7), AccountDeletionRules.Window);
        Assert.Equal(Requested.AddDays(7), AccountDeletionRules.ScheduledFor(Requested));
    }

    [Fact]
    public void AnAccountIsDueExactlyWhenTheWindowIsOver()
    {
        Assert.False(AccountDeletionRules.IsDue(null, Requested.AddDays(30)));
        Assert.False(AccountDeletionRules.IsDue(Requested, Requested.AddDays(7).AddTicks(-1)));
        Assert.True(AccountDeletionRules.IsDue(Requested, Requested.AddDays(7)));
        Assert.True(AccountDeletionRules.IsDue(Requested, Requested.AddDays(8)));
    }

    [Fact]
    public void CancellingIsPossibleOnlyInsideTheWindow()
    {
        Assert.False(AccountDeletionRules.CanCancel(null, Requested));
        Assert.True(AccountDeletionRules.CanCancel(Requested, Requested));
        Assert.True(AccountDeletionRules.CanCancel(Requested, Requested.AddDays(7).AddTicks(-1)));
        Assert.False(AccountDeletionRules.CanCancel(Requested, Requested.AddDays(7)));
    }

    [Theory]
    [InlineData("SİL", true)]
    [InlineData("sil", true)]
    [InlineData("SIL", true)]
    [InlineData("Sil", true)]
    [InlineData("sİl", true)]
    [InlineData("sıl", true)]
    [InlineData("  SİL  ", true)]
    [InlineData("SİLL", false)]
    [InlineData("SI L", false)]
    [InlineData("delete", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    public void TheConfirmationWordAcceptsTheCasingsAPhoneKeyboardMakes(string? confirm, bool expected)
    {
        Assert.Equal(expected, AccountDeletionRules.ConfirmationMatches(confirm));
    }

    [Fact]
    public void TheConfirmationWordIsTheTurkishOne()
    {
        Assert.Equal("SİL", AccountDeletionRules.ConfirmWord);
        Assert.True(AccountDeletionRules.ConfirmationMatches(AccountDeletionRules.ConfirmWord));
    }

    [Fact]
    public void TheDeleteRequestNeedsTheWordAndBoundsThePassword()
    {
        var validator = new DeleteAccountValidator();

        Assert.True(validator.Validate(new DeleteAccountRequest("Passw0rd-ok", "SİL")).IsValid);
        Assert.True(validator.Validate(new DeleteAccountRequest(null, "sil")).IsValid);
        Assert.False(validator.Validate(new DeleteAccountRequest("Passw0rd-ok", null)).IsValid);
        Assert.False(validator.Validate(new DeleteAccountRequest("Passw0rd-ok", "yes")).IsValid);
        Assert.False(validator.Validate(new DeleteAccountRequest(new string('a', 129), "SİL")).IsValid);
    }

    [Fact]
    public void TheCancelRequestNeedsOneKindOfCredentials()
    {
        var validator = new CancelDeletionValidator();

        Assert.True(validator.Validate(new CancelDeletionRequest(Email: "ada@example.com", Password: "Passw0rd-ok")).IsValid);
        Assert.True(validator.Validate(new CancelDeletionRequest(Provider: "google", IdToken: "token")).IsValid);
        Assert.True(validator.Validate(new CancelDeletionRequest(Provider: "APPLE", IdToken: "token", Nonce: "n")).IsValid);
        Assert.False(validator.Validate(new CancelDeletionRequest()).IsValid);
        Assert.False(validator.Validate(new CancelDeletionRequest(Email: "ada@example.com")).IsValid);
        Assert.False(validator.Validate(new CancelDeletionRequest(Email: "not-an-email", Password: "x")).IsValid);
        Assert.False(validator.Validate(new CancelDeletionRequest(Provider: "facebook", IdToken: "token")).IsValid);
        Assert.False(validator.Validate(new CancelDeletionRequest(IdToken: "token")).IsValid);
        Assert.False(validator.Validate(new CancelDeletionRequest(Provider: "google", IdToken: new string('a', 9000))).IsValid);
    }

    [Fact]
    public void ThePendingErrorCarriesTheDateTheDataWillBeGone()
    {
        var user = new Kombin.Api.Persistence.Entities.Identity.User { DeletionRequestedAt = Requested };

        var error = AccountDeletion.PendingError(user);

        Assert.Equal("Identity.AccountPendingDeletion", error.Code);
        Assert.Equal(Requested.AddDays(7), error.Extensions!["deletionScheduledFor"]);
    }

    [Fact]
    public void APostHiddenByAPendingDeletionIsGoneForItsAuthorToo()
    {
        foreach (var visibility in Enum.GetValues<PostVisibility>())
        {
            Assert.False(PostRules.CanView(new PostViewFacts(PostStatus.AccountPending, visibility, IsAuthor: true, ViewerFollowsAuthor: false, BlockedEitherWay: false)));
            Assert.False(PostRules.CanView(new PostViewFacts(PostStatus.AccountPending, visibility, IsAuthor: false, ViewerFollowsAuthor: true, BlockedEitherWay: false)));
        }

        Assert.True(PostRules.CanView(new PostViewFacts(PostStatus.Live, PostVisibility.Private, IsAuthor: true, ViewerFollowsAuthor: false, BlockedEitherWay: false)));
        Assert.False(PostRules.IsReviewable(PostStatus.AccountPending));
        Assert.False(PostRules.CanPublish(PostStatus.AccountPending));
        Assert.False(PurgeRules.IsDue(PostStatus.AccountPending, null, Requested.AddYears(-1), Requested.AddYears(-1), Requested));
    }
}
