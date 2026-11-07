using Kombin.Api.Common.Results;

namespace Kombin.Api.Features.Social;

internal static class SocialErrors
{
    // Also the answer for a post the caller may not see: whether it exists is none of their business.
    public static readonly Error PostNotFound =
        Error.NotFound("Social.PostNotFound", "The post does not exist.");

    public static readonly Error UserNotFound =
        Error.NotFound("Social.UserNotFound", "The user does not exist.");

    public static readonly Error CreatorNotFound =
        Error.NotFound("Social.CreatorNotFound", "There is no creator with this handle.");

    public static readonly Error ConsentRequired =
        Error.Forbidden("Social.ConsentRequired", $"Accept the current posting consent ({PostingConsent.Current.Version}) before posting.");

    public static readonly Error ConsentOutdated =
        Error.Validation("Social.ConsentOutdated", $"Only the current posting consent ({PostingConsent.Current.Version}) can be accepted.");

    public static readonly Error UploadMissing =
        Error.Conflict("Social.UploadMissing", "The photo has not been uploaded yet; PUT it to the upload URL first.");

    public static readonly Error ImageTooLarge =
        Error.Validation("Social.ImageTooLarge", $"The photo must not be larger than {SocialLimits.MaxImageBytes / (1024 * 1024)} MB.");

    public static readonly Error PostProcessing =
        Error.Conflict("Social.PostProcessing", "The post is still being checked; try again in a few seconds.");

    public static readonly Error PostNotPublishable =
        Error.Conflict("Social.PostNotPublishable", "The post cannot be published in its current state.");

    public static readonly Error OutfitNotFound =
        Error.NotFound("Social.OutfitNotFound", "The outfit does not exist or is not saved.");

    public static readonly Error InvalidTagTarget =
        Error.Validation("Social.InvalidTagTarget", "A tag points to a wardrobe item that is not yours or a product that does not exist.");

    public static readonly Error CannotTargetSelf =
        Error.Validation("Social.CannotTargetSelf", "You cannot do this to yourself or your own post.");

    public static readonly Error UserBlocked =
        Error.Conflict("Social.UserBlocked", "There is a block between you and this user.");

    public static readonly Error AlreadyReported =
        Error.Conflict("Social.AlreadyReported", "You have already reported this post.");

    public static readonly Error FollowingRequiresAuth =
        Error.Unauthorized("Social.FollowingRequiresAuth", "Sign in to see the posts of the people you follow.");

    public static readonly Error HandleTaken =
        Error.Conflict("Social.HandleTaken", "Another creator already uses this handle.");

    public static readonly Error NotReviewable =
        Error.Conflict("Social.NotReviewable", "Only hidden posts and posts waiting for review can be reviewed.");

    public static readonly Error NotACreator =
        Error.Forbidden("Social.NotACreator", "Creator statistics are for creators.");

    public static readonly Error NothingToTry =
        Error.Conflict("Social.NothingToTry", "The post has no tagged catalog product to try with your wardrobe.");
}

internal static class SocialLimits
{
    /// <summary>The largest photo accepted, the same as the wardrobe's and the vision service's.</summary>
    public const long MaxImageBytes = 8 * 1024 * 1024;

    public const int MaxTags = 6;

    /// <summary>JPEG, PNG and WEBP only: the metadata stripper reads these (HEIC is converted by the app).</summary>
    public static readonly IReadOnlyList<string> ImageContentTypes = ["image/jpeg", "image/png", "image/webp"];

    public static readonly TimeSpan UploadUrlLifetime = TimeSpan.FromMinutes(15);

    /// <summary>Post images are private objects served through presigned GETs of this lifetime.</summary>
    public static readonly TimeSpan ImageUrlLifetime = TimeSpan.FromHours(1);

    public static readonly TimeSpan ReprocessAfter = TimeSpan.FromMinutes(10);

    public const int PageSize = 20;

    /// <summary>Storage area of post images: <c>posts/{userId}/{postId}/{original|feed|preview}.{ext}</c>, all private.</summary>
    public const string StorageArea = "posts";
}

/// <summary>What users read (Turkish).</summary>
internal static class SocialCopy
{
    public const string Blocked = "Fotoğraf topluluk kurallarına uymuyor.";

    public const string InReview = "Fotoğrafın kısa bir incelemeden geçiyor; onaylanınca yayınlanacak.";

    public const string RemovedAfterReview = "Gönderi inceleme sonucunda kaldırıldı: topluluk kurallarına uymuyor.";

    public const string HiddenAfterReports = "Gönderin şikâyetler nedeniyle incelemede; şimdilik başkalarına görünmüyor.";

    public const string ProcessingFailed = "Fotoğraf işlenemedi; lütfen başka bir fotoğrafla tekrar dene.";

    public const string CreatorDisclosure = "Bu içerikten komisyon kazanabilir";
}
