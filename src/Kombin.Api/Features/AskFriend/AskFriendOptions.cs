using Kombin.Api.Common.Hosting;
using Kombin.Api.Common.Results;
using Microsoft.Extensions.Options;

namespace Kombin.Api.Features.AskFriend;

/// <summary>
/// <c>AskFriend:*</c>: every number of "Arkadaşına sor" (design: <c>docs/design/ask-a-friend.md</c>, section 8). <c>AskFriendOptionsTests</c> pins the
/// defaults to the design note.
/// </summary>
internal sealed class AskFriendOptions : IFeatureOptions
{
    public const string SectionName = "AskFriend";

    /// <summary>Off until the app screens ship: every ask route answers 404 <c>AskFriend.Disabled</c>, the page answers 404 and no job runs.</summary>
    public bool Enabled { get; set; }

    static Error IFeatureOptions.Disabled => AskFriendErrors.Disabled;

    /// <summary>The public origin the share link is built on (<c>{PublicBaseUrl}/sor/{token}</c>); required and https in production.</summary>
    public string? PublicBaseUrl { get; set; }

    /// <summary>How long a link takes answers. The consent text names it: changing it needs a new consent version.</summary>
    public TimeSpan AnswerWindow { get; set; } = TimeSpan.FromHours(24);

    public int MaxAsksPerDay { get; set; } = 10;

    /// <summary>Answers an ask keeps; a further browser is refused.</summary>
    public int MaxAnswers { get; set; } = 50;

    /// <summary>A known birth year keeps the asker out unless they are certainly older than this (<c>AgeRule.IsUnderAge</c>).</summary>
    public int MinAge { get; set; } = 18;

    /// <summary>How long the presigned picture URLs on the public page live.</summary>
    public TimeSpan ImageUrlLifetime { get; set; } = TimeSpan.FromMinutes(15);

    public AskFriendReports Reports { get; set; } = new();

    public AskFriendRetention Retention { get; set; } = new();

    public AskFriendStoreLinks StoreLinks { get; set; } = new();
}

internal sealed class AskFriendReports
{
    /// <summary>Distinct browsers' reports that hide an ask.</summary>
    public int HideAt { get; set; } = 3;
}

internal sealed class AskFriendRetention
{
    /// <summary>Asks (with pieces, answers and reports) are deleted this many days after creation.</summary>
    public int AskDays { get; set; } = 30;

    /// <summary>A stored copy under <c>ask/</c> that no piece names is deleted once it is this old (a younger one may belong to an ask whose transaction has not committed yet).</summary>
    public TimeSpan OrphanGrace { get; set; } = TimeSpan.FromDays(1);
}

internal sealed class AskFriendStoreLinks
{
    public string? Android { get; set; }

    public string? Ios { get; set; }
}

internal sealed class AskFriendOptionsValidator : IValidateOptions<AskFriendOptions>
{
    public ValidateOptionsResult Validate(string? name, AskFriendOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = Problems(options).ToList();

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    /// <summary>Every broken rule at once, so a bad configuration is fixed in one go.</summary>
    internal static IEnumerable<string> Problems(AskFriendOptions options)
    {
        if (options.AnswerWindow <= TimeSpan.Zero)
        {
            yield return "AskFriend:AnswerWindow must be positive.";
        }

        if (options.MaxAsksPerDay < 1 || options.MaxAnswers < 1)
        {
            yield return "AskFriend:MaxAsksPerDay and MaxAnswers must be at least 1.";
        }

        if (options.MinAge < 0)
        {
            yield return "AskFriend:MinAge must not be negative.";
        }

        if (options.ImageUrlLifetime <= TimeSpan.Zero)
        {
            yield return "AskFriend:ImageUrlLifetime must be positive.";
        }

        if (options.Reports.HideAt < 1)
        {
            yield return "AskFriend:Reports:HideAt must be at least 1.";
        }

        if (options.Retention.AskDays < 1 || options.Retention.OrphanGrace <= TimeSpan.Zero)
        {
            yield return "AskFriend:Retention:AskDays must be at least 1 and OrphanGrace positive.";
        }

        if (!string.IsNullOrWhiteSpace(options.PublicBaseUrl) && !IsHttpUrl(options.PublicBaseUrl))
        {
            yield return "AskFriend:PublicBaseUrl must be an absolute http or https URL when set.";
        }
    }

    private static bool IsHttpUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
