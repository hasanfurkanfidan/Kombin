using Kombin.Api.Features.Admin;
using Kombin.Api.Features.Admin.Pages;
using Kombin.Api.Features.Social;
using Kombin.Api.Persistence.Entities.Identity;
using Kombin.Api.Persistence.Entities.Social;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Kombin.Api.Tests;

public class AdminLoginThrottleTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 14, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TheFifthFailureLocksTheAddressUntilTheWindowPasses()
    {
        var clock = new FixedClock(Start);
        var throttle = new AdminLoginThrottle(clock);

        for (var failure = 1; failure <= AdminLoginThrottle.MaxFailures; failure++)
        {
            Assert.False(throttle.IsLocked("1.2.3.4"));
            throttle.RegisterFailure("1.2.3.4");
            clock.UtcNow = clock.UtcNow.AddSeconds(10);
        }

        Assert.True(throttle.IsLocked("1.2.3.4"));
        Assert.False(throttle.IsLocked("5.6.7.8"));

        // The oldest failure falls out of the 15-minute window, so one slot is free again.
        clock.UtcNow = Start.Add(AdminLoginThrottle.Window).AddSeconds(1);
        Assert.False(throttle.IsLocked("1.2.3.4"));
    }

    [Fact]
    public void FailuresOutsideTheWindowDoNotCountAndASuccessClearsThem()
    {
        var clock = new FixedClock(Start);
        var throttle = new AdminLoginThrottle(clock);

        for (var failure = 0; failure < AdminLoginThrottle.MaxFailures - 1; failure++)
        {
            throttle.RegisterFailure("a");
        }

        clock.UtcNow = Start.AddMinutes(16);
        throttle.RegisterFailure("a");
        Assert.False(throttle.IsLocked("a"));

        for (var failure = 0; failure < AdminLoginThrottle.MaxFailures; failure++)
        {
            throttle.RegisterFailure("b");
        }

        Assert.True(throttle.IsLocked("b"));
        throttle.Reset("b");
        Assert.False(throttle.IsLocked("b"));
    }
}

public class AdminCredentialsTests
{
    private static readonly string Hash = AdminHashCommand.Hash("Correct-horse-battery-1");

    private static AdminCredentials Credentials(params (string Email, string Hash)[] users) => new(
        new TestOptionsMonitor(new AdminOptions { Users = [.. users.Select(user => new AdminUserOptions { Email = user.Email, PasswordHash = user.Hash })] }),
        new PasswordHasher<User>());

    [Fact]
    public void TheRightPairPassesCaseInsensitivelyOnTheEmailAndReturnsTheConfiguredEmail()
    {
        var credentials = Credentials(("Founder@Kombin.App", Hash));

        Assert.Equal("Founder@Kombin.App", credentials.Verify(" founder@kombin.app ", "Correct-horse-battery-1"));
    }

    [Theory]
    [InlineData("founder@kombin.app", "wrong")]
    [InlineData("founder@kombin.app", "")]
    [InlineData("someone@kombin.app", "Correct-horse-battery-1")]
    [InlineData("", "Correct-horse-battery-1")]
    [InlineData(null, null)]
    public void AnythingElseFails(string? email, string? password) =>
        Assert.Null(Credentials(("founder@kombin.app", Hash)).Verify(email, password));

    [Fact]
    public void AnEntryWithoutAHashNeverSignsAnyoneIn() =>
        Assert.Null(Credentials(("founder@kombin.app", string.Empty)).Verify("founder@kombin.app", string.Empty));

    [Fact]
    public void AMalformedHashDoesNotThrowButFails()
    {
        // The Identity hasher answers Failed for a hash it cannot read; a broken config entry must not become a 500 on the login page.
        Assert.Null(Credentials(("founder@kombin.app", "not-a-hash")).Verify("founder@kombin.app", "Correct-horse-battery-1"));
    }

    private sealed class TestOptionsMonitor(AdminOptions value) : IOptionsMonitor<AdminOptions>
    {
        public AdminOptions CurrentValue => value;

        public AdminOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<AdminOptions, string?> listener) => null;
    }
}

public class AdminOptionsValidatorTests
{
    private static ValidateOptionsResult Validate(params (string Email, string Hash)[] users) =>
        new AdminOptionsValidator().Validate(null, new AdminOptions { Users = [.. users.Select(user => new AdminUserOptions { Email = user.Email, PasswordHash = user.Hash })] });

    [Fact]
    public void NobodyConfiguredIsValidBecauseTheSafeDefaultIsNoAdmin()
    {
        Assert.True(Validate().Succeeded);
        Assert.True(Validate(("", "")).Succeeded);
    }

    [Fact]
    public void AnEntryNeedsBothAnEmailAndAHash()
    {
        Assert.True(Validate(("founder@kombin.app", "hash")).Succeeded);
        Assert.False(Validate(("founder@kombin.app", "")).Succeeded);
        Assert.False(Validate(("", "hash")).Succeeded);
        Assert.False(Validate(("not-an-email", "hash")).Succeeded);
    }

    [Fact]
    public void TheSameEmailTwiceIsRefused()
    {
        var result = Validate(("a@kombin.app", "h1"), (" A@kombin.app", "h2"));

        Assert.True(result.Failed);
        Assert.Contains("listed twice", result.FailureMessage, StringComparison.Ordinal);
    }
}

public class AdminHashCommandTests
{
    [Fact]
    public void TheHashIsTheIdentityHashersFormatAndVerifies()
    {
        var hash = AdminHashCommand.Hash("Correct-horse-battery-1");

        var hasher = new PasswordHasher<User>();
        Assert.Equal(PasswordVerificationResult.Success, hasher.VerifyHashedPassword(new User(), hash, "Correct-horse-battery-1"));
        Assert.Equal(PasswordVerificationResult.Failed, hasher.VerifyHashedPassword(new User(), hash, "other"));
        Assert.DoesNotContain('$', hash);
    }

    [Fact]
    public void TheCommandPrintsAHashForAnArgumentOrStandardInput()
    {
        var fromArgument = new StringWriter();
        var fromInput = new StringWriter();

        Assert.Equal(0, AdminHashCommand.Run(["hash-password", "Correct-horse-battery-1"], fromArgument, new StringWriter(), () => throw new InvalidOperationException()));
        Assert.Equal(0, AdminHashCommand.Run(["hash-password"], fromInput, new StringWriter(), () => "Correct-horse-battery-1"));

        foreach (var output in new[] { fromArgument, fromInput })
        {
            Assert.Equal(PasswordVerificationResult.Success, new PasswordHasher<User>().VerifyHashedPassword(new User(), output.ToString().Trim(), "Correct-horse-battery-1"));
        }
    }

    [Fact]
    public void AShortOrMissingPasswordIsRefused()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        Assert.Equal(1, AdminHashCommand.Run(["hash-password", "short"], output, error, () => null));
        Assert.Equal(1, AdminHashCommand.Run(["hash-password"], output, error, () => null));
        Assert.Equal(string.Empty, output.ToString());
        Assert.Contains("at least 12", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyTheExactCommandNameIsRecognised()
    {
        Assert.True(AdminHashCommand.IsRequested(["hash-password"]));
        Assert.False(AdminHashCommand.IsRequested([]));
        Assert.False(AdminHashCommand.IsRequested(["Hash-Password"]));
        Assert.False(AdminHashCommand.IsRequested(["--applicationName", "x"]));
    }
}

public class AdminCookieOptionsTests
{
    private static CookieAuthenticationOptions Options(string environment)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new TestEnvironment(environment));
        services.AddAdminFeature();

        return services.BuildServiceProvider().GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(AdminAuth.Scheme);
    }

    [Theory]
    [InlineData("Development", CookieSecurePolicy.SameAsRequest)]
    [InlineData("Production", CookieSecurePolicy.Always)]
    [InlineData("Staging", CookieSecurePolicy.Always)]
    public void TheSessionCookieIsHttpOnlyStrictAndSecureOutsideDevelopment(string environment, CookieSecurePolicy secure)
    {
        var options = Options(environment);

        Assert.True(options.Cookie.HttpOnly);
        Assert.Equal(SameSiteMode.Strict, options.Cookie.SameSite);
        Assert.Equal(secure, options.Cookie.SecurePolicy);
        Assert.Equal(TimeSpan.FromHours(8), options.ExpireTimeSpan);
        Assert.True(options.SlidingExpiration);
        Assert.Equal("/admin/login", options.LoginPath.Value);
    }

    private sealed class TestEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "Kombin.Api";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

public class AdminLoginReturnUrlTests
{
    [Theory]
    [InlineData("/admin", "/admin")]
    [InlineData("/admin/review", "/admin/review")]
    [InlineData("/admin/users?q=a", "/admin/users?q=a")]
    [InlineData("/ADMIN/audit", "/ADMIN/audit")]
    [InlineData(null, "/admin")]
    [InlineData("", "/admin")]
    [InlineData("https://evil.example/admin", "/admin")]
    [InlineData("//evil.example/admin", "/admin")]
    [InlineData("/admin//evil.example", "/admin")]
    [InlineData("/adminx", "/admin")]
    [InlineData("/api/v1/me", "/admin")]
    [InlineData("/admin\\evil", "/admin")]
    public void OnlyAPathInsideThePanelIsFollowed(string? returnUrl, string expected) =>
        Assert.Equal(expected, LoginModel.SafeReturnUrl(returnUrl));
}

public class DashboardQueriesTests
{
    [Fact]
    public void WindowedQueriesTakeSinceAndTheOthersDoNot()
    {
        foreach (var metric in DashboardQueries.Metrics)
        {
            Assert.Equal(metric.Windowed, metric.Sql.Contains("@since", StringComparison.Ordinal));
            Assert.False(string.IsNullOrWhiteSpace(metric.Meaning));
            Assert.StartsWith("SELECT ", metric.Sql, StringComparison.Ordinal);
        }

        foreach (var breakdown in DashboardQueries.Breakdowns)
        {
            Assert.Contains("@since", breakdown.Sql, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void KeysAreUniqueAndReadOnlyQueriesNeverWrite()
    {
        var keys = DashboardQueries.Metrics.Select(metric => metric.Key).Concat(DashboardQueries.Breakdowns.Select(breakdown => breakdown.Key)).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());

        var all = DashboardQueries.Metrics.Select(metric => metric.Sql).Concat(DashboardQueries.Breakdowns.Select(breakdown => breakdown.Sql)).Append(DashboardQueries.DatabaseSizeSql);
        foreach (var sql in all)
        {
            Assert.DoesNotContain("INSERT", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("UPDATE", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("DELETE", sql, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void TodayStartsAtMidnightInIstanbulAndTheWeekIsRolling()
    {
        // 2026-11-14 22:30 UTC is 01:30 on the 15th in Istanbul (UTC+3): today began 2026-11-14 21:00 UTC.
        var now = new DateTimeOffset(2026, 11, 14, 22, 30, 0, TimeSpan.Zero);

        var (today, week) = AdminDashboard.Windows(now);

        Assert.Equal(new DateTimeOffset(2026, 11, 14, 21, 0, 0, TimeSpan.Zero), today);
        Assert.Equal(now.AddDays(-7), week);
    }
}

public class AdminFormatTests
{
    [Fact]
    public void TimesAreIstanbulTimeAndMissingOnesADash()
    {
        Assert.Equal("2026-11-14 18:30", AdminFormat.Time(new DateTimeOffset(2026, 11, 14, 15, 30, 0, TimeSpan.Zero)));
        Assert.Equal("-", AdminFormat.Time(null));
    }

    [Theory]
    [InlineData(null, "-")]
    [InlineData(512L, "512 B")]
    [InlineData(2048L, "2 KB")]
    [InlineData(1_572_864L, "1.5 MB")]
    [InlineData(5_368_709_120L, "5 GB")]
    public void BytesAreHumanReadable(long? bytes, string expected) => Assert.Equal(expected, AdminFormat.Bytes(bytes));

    [Fact]
    public void AgoSaysHowStaleSomethingIs()
    {
        var now = new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal("never", AdminFormat.Ago(null, now));
        Assert.Equal("just now", AdminFormat.Ago(now.AddSeconds(-20), now));
        Assert.Equal("5 min ago", AdminFormat.Ago(now.AddMinutes(-5), now));
        Assert.Equal("3 h ago", AdminFormat.Ago(now.AddHours(-3), now));
        Assert.Equal("2 d ago", AdminFormat.Ago(now.AddDays(-2), now));
        Assert.Equal("in the future", AdminFormat.Ago(now.AddMinutes(1), now));
    }

    [Fact]
    public void MoneyAndMaskedEmails()
    {
        Assert.Equal("1,234.50 ₺", AdminFormat.Money(1234.5m));
        Assert.Equal("f***@gmail.com", AdminFormat.MaskEmail("furkan@gmail.com"));
        Assert.Equal("***", AdminFormat.MaskEmail("not-an-email"));
    }
}

public class ModerationViewTests
{
    [Fact]
    public void APhotoPostHasOneLineWithItsLabelsAndScores()
    {
        const string Json = """{"photo":{"verdict":"review","labels":["nsfw"],"scores":{"nsfw":0.91,"violence":0.02}},"layout":"person"}""";

        var line = Assert.Single(ModerationView.Parse(Json));

        Assert.Equal(("photo", "review"), (line.Subject, line.Verdict));
        Assert.Equal(["nsfw"], line.Labels);
        Assert.Equal(KeyValuePair.Create("nsfw", 0.91), line.Scores[0]);
        Assert.Equal(2, line.Scores.Count);
    }

    [Fact]
    public void AnOutfitPostHasOneLinePerPiece()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var json = "{\"pieces\":{\"" + first + "\":{\"verdict\":\"allow\",\"labels\":[],\"scores\":{}},\"" + second + "\":{\"verdict\":\"block\",\"labels\":[\"x\"],\"scores\":{\"x\":1}}}}";

        var lines = ModerationView.Parse(json);

        Assert.Equal(2, lines.Count);
        Assert.Equal(["allow", "block"], lines.Select(line => line.Verdict));
        Assert.StartsWith("piece ", lines[0].Subject, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("""{"photo":"oops"}""")]
    [InlineData("{}")]
    public void AnythingElseParsesToNoLinesInsteadOfFailing(string? json) => Assert.Empty(ModerationView.Parse(json));
}

public class BlockReasonsTests
{
    [Fact]
    public void TheDefaultIsTheStandardRemovalCopyAndAnUnknownKeyFallsBackToIt()
    {
        Assert.Equal(SocialCopy.RemovedAfterReview, BlockReasons.Compose(BlockReasons.Default, null));
        Assert.Equal(SocialCopy.RemovedAfterReview, BlockReasons.Compose("no-such-reason", "   "));
        Assert.Equal(SocialCopy.RemovedAfterReview, BlockReasons.Compose(null, null));
    }

    [Fact]
    public void AListReasonAndTheFoundersOwnWordsAreJoined()
    {
        var spam = BlockReasons.All.Single(reason => reason.Key == "spam").Value;

        Assert.Equal(spam, BlockReasons.Compose("spam", null));
        Assert.Equal($"{spam} Tekrar etme.", BlockReasons.Compose("spam", "  Tekrar etme. "));
    }

    [Fact]
    public void EveryListedReasonFitsTheColumnWithRoomForSomeOwnWords()
    {
        foreach (var reason in BlockReasons.All)
        {
            Assert.True(reason.Value.Length <= Post.MaxModerationReasonLength - 120 - 1, $"'{reason.Key}' leaves no room for the 120 characters the form allows.");
        }
    }
}

public class ReviewRulesTests
{
    [Fact]
    public void KeepIsNotATransitionAndRemovalCutsAtTheColumnLength()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PostRules.StatusAfterReview(ReviewDecision.Keep, confirmed: true));
        Assert.Equal(SocialCopy.RemovedAfterReview, Kombin.Api.Features.Social.Review.PostReviewer.RemovalReason("  "));
        Assert.Equal("Kendi nedenimiz.", Kombin.Api.Features.Social.Review.PostReviewer.RemovalReason(" Kendi nedenimiz. "));
        Assert.Equal(Post.MaxModerationReasonLength, Kombin.Api.Features.Social.Review.PostReviewer.RemovalReason(new string('x', 500)).Length);
    }
}
