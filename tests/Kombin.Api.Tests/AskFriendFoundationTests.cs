using System.Security.Cryptography;
using System.Text;
using Kombin.Api.Common.Storage;
using Kombin.Api.Features.AskFriend;
using Kombin.Api.Persistence.Entities.AskFriend;

namespace Kombin.Api.Tests;

/// <summary>The defaults are the numbers of <c>docs/design/ask-a-friend.md</c> (section 8); change one there and here together.</summary>
public class AskFriendOptionsTests
{
    [Fact]
    public void AskFriendOptionsDefaultsMatchTheDesignNote()
    {
        var options = new AskFriendOptions();

        Assert.False(options.Enabled);
        Assert.Null(options.PublicBaseUrl);
        Assert.Equal(
            TimeSpan.FromHours(24),
            options.AnswerWindow);
        Assert.True(
            AskFriendConsent.Current.Text.Contains("24 saat", StringComparison.Ordinal),
            "The consent text names the 24-hour window: changing AnswerWindow needs a new consent version (append one to AskFriendConsent.Versions).");
        Assert.Equal((10, 50, 18), (options.MaxAsksPerDay, options.MaxAnswers, options.MinAge));
        Assert.Equal(TimeSpan.FromMinutes(15), options.ImageUrlLifetime);
        Assert.Equal(3, options.Reports.HideAt);
        Assert.Equal((30, TimeSpan.FromDays(1)), (options.Retention.AskDays, options.Retention.OrphanGrace));
        Assert.Null(options.StoreLinks.Android);
        Assert.Null(options.StoreLinks.Ios);
    }

    [Fact]
    public void TheDefaultsAreValid() => Assert.Empty(AskFriendOptionsValidator.Problems(new AskFriendOptions()));

    [Fact]
    public void EveryBrokenRuleIsListedAtOnce()
    {
        var options = new AskFriendOptions
        {
            PublicBaseUrl = "kombin.app/path",
            AnswerWindow = TimeSpan.Zero,
            MaxAsksPerDay = 0,
            MinAge = -1,
            ImageUrlLifetime = TimeSpan.FromSeconds(-1),
            Reports = new AskFriendReports { HideAt = 0 },
            Retention = new AskFriendRetention { AskDays = 0 },
        };

        Assert.Equal(7, AskFriendOptionsValidator.Problems(options).Count());
        Assert.False(new AskFriendOptionsValidator().Validate(null, options).Succeeded);
    }

    [Theory]
    [InlineData("https://kombin.app", true)]
    [InlineData("http://localhost:5080", true)]
    [InlineData("ftp://kombin.app", false)]
    [InlineData("/relative", false)]
    public void ThePublicBaseUrlMustBeAnAbsoluteHttpUrl(string url, bool valid) =>
        Assert.Equal(valid, !AskFriendOptionsValidator.Problems(new AskFriendOptions { PublicBaseUrl = url }).Any());
}

public class AskFriendConsentTests
{
    /// <summary>Published texts are pinned: editing one in place fails here; add a new version instead (every asker then accepts again).</summary>
    private static readonly Dictionary<string, string> PublishedHashes = new()
    {
        ["2026-11-v1"] = "a8c9520e6ab3e839cbec4b5b0216056bcdc23eca73af5b10c4201d570a890695",
    };

    [Fact]
    public void AskFriendConsentTextHashIsPinned()
    {
        foreach (var version in AskFriendConsent.Versions)
        {
            Assert.True(PublishedHashes.TryGetValue(version.Version, out var hash), $"Pin the hash of the new ask consent version {version.Version} here.");
            Assert.Equal(hash, version.Hash);
        }

        Assert.Equal("2026-11-v1", AskFriendConsent.Current.Version);
        Assert.Equal(AskFriendConsent.Versions.Count, AskFriendConsent.Versions.Select(version => version.Version).Distinct().Count());
        Assert.EndsWith("Linki paylaşarak 18 yaşından büyük olduğunu onaylarsın.", AskFriendConsent.Current.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryVersionKeepsToTheGlossary() =>
        Assert.All(AskFriendConsent.Versions, version => Assert.Empty(Kombin.Api.Common.Text.CopyGlossary.Violations(version.Text)));
}

public class AskTokenTests
{
    [Fact]
    public void AskTokenIs22UrlSafeCharacters()
    {
        var tokens = Enumerable.Range(0, 200).Select(_ => AskToken.New()).ToList();

        Assert.All(tokens, token =>
        {
            Assert.Equal(22, token.Length);
            Assert.True(AskToken.IsWellFormed(token), token);
            Assert.DoesNotContain('=', token);
            Assert.DoesNotContain('+', token);
            Assert.DoesNotContain('/', token);
        });
        Assert.Equal(tokens.Count, tokens.Distinct().Count());
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("abcdefghijklmnopqrstu", false)]
    [InlineData("abcdefghijklmnopqrstuvw", false)]
    [InlineData("abcdefghijklmnopqrstu+", false)]
    [InlineData("abcdefghijklmnopqrst=/", false)]
    [InlineData("abcdefghijklmnopqrstuv", true)]
    [InlineData("AB-_efghijklmnopqrst09", true)]
    public void OnlyTwentyTwoUrlSafeCharactersAreWellFormed(string? value, bool expected) => Assert.Equal(expected, AskToken.IsWellFormed(value));
}

public class AskStateTests
{
    private static readonly DateTimeOffset Created = new(2026, 11, 20, 8, 0, 0, TimeSpan.Zero);

    private static Ask AskWith(AskStatus status) => new() { Status = status, CreatedAt = Created, ExpiresAt = Created.AddHours(24) };

    [Fact]
    public void AskStateIsExpiredAfterTheAnswerWindow()
    {
        var ask = AskWith(AskStatus.Open);

        Assert.Equal(AskViewState.Open, AskState.Of(ask, Created));
        Assert.Equal(AskViewState.Open, AskState.Of(ask, Created.AddHours(24).AddTicks(-1)));
        Assert.Equal(AskViewState.Expired, AskState.Of(ask, Created.AddHours(24)));
        Assert.Equal(AskViewState.Expired, AskState.Of(ask, Created.AddDays(3)));
    }


    [Fact]
    public void ClosedAndHiddenWinOverExpired()
    {
        foreach (var (status, expected) in new[] { (AskStatus.Closed, AskViewState.Closed), (AskStatus.Hidden, AskViewState.Hidden) })
        {
            var ask = AskWith(status);

            Assert.Equal(expected, AskState.Of(ask, Created.AddHours(1)));
            Assert.Equal(expected, AskState.Of(ask, Created.AddDays(3)));
        }
    }
}

public class AskFriendStorageTests
{
    [Fact]
    public void SharedImageVariantIsNotPublic()
    {
        var key = StorageKeys.Build("ask", Guid.NewGuid(), Guid.NewGuid(), ImageVariant.Shared, "webp");

        Assert.EndsWith("/shared.webp", key, StringComparison.Ordinal);
        Assert.StartsWith("ask/", key, StringComparison.Ordinal);
        Assert.False(StorageKeys.IsPublic(key));
        Assert.Equal(StorageKeys.PrivateCacheControl, StorageKeys.CacheControlFor(key));
    }
}

public class AskFriendCopyTests
{
    [Fact]
    public void TheShareTextAsksOneThingPerKind()
    {
        Assert.Equal("Bugün hangisini giyeyim? Bir dokunuşla cevap ver:", AskFriendCopy.ShareText(AskKind.Pair));
        Assert.Equal("Bu kombin olur mu? Bir dokunuşla cevap ver:", AskFriendCopy.ShareText(AskKind.Single));
        Assert.Equal("Link bildirimler yüzünden kapatıldı.", AskFriendCopy.HiddenStatusMessage);
    }
}
