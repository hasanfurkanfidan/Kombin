using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Kombin.Api.Common.Storage;
using Kombin.Api.Features.Analytics;
using Kombin.Api.Features.Social;
using Kombin.Api.Persistence.Entities.Recommendation;
using Kombin.Api.Persistence.Entities.Social;

namespace Kombin.Api.Tests;

public class FeedRankingTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    private static FeedCandidate Post(double hoursAgo, int likes = 0, bool creator = false, int? match = null, Guid? author = null, Guid? id = null) =>
        new(id ?? Guid.NewGuid(), author ?? Guid.NewGuid(), Now.AddHours(-hoursAgo), likes, creator, match, FeedReason.ForYou);

    [Fact]
    public void RecencyDecaysWithAFortyEightHourTimeConstant()
    {
        Assert.Equal(1, FeedRanking.Recency(Now, Now));
        Assert.Equal(Math.Exp(-0.5), FeedRanking.Recency(Now.AddHours(-24), Now), 6);
        Assert.Equal(Math.Exp(-1), FeedRanking.Recency(Now.AddHours(-48), Now), 6);
        Assert.Equal(1, FeedRanking.Recency(Now.AddHours(1), Now));
    }

    [Fact]
    public void LikesCountLogarithmically()
    {
        Assert.Equal(1, FeedRanking.Popularity(0));
        Assert.Equal(1 + (Math.Log(11) / 4), FeedRanking.Popularity(10), 6);
        Assert.Equal(2.15, FeedRanking.Popularity(100), 2);
        Assert.Equal(2.73, FeedRanking.Popularity(1000), 2);
        Assert.Equal(1, FeedRanking.Popularity(-5));
    }

    [Fact]
    public void TheCreatorBoostIsAtMostThirtyPercent()
    {
        var plain = Post(5, likes: 10);
        var creator = plain with { AuthorIsCreator = true };

        Assert.Equal(1.3, FeedRanking.Score(creator, Now) / FeedRanking.Score(plain, Now), 6);
        Assert.True(FeedRanking.CreatorBoost <= 1.3);
    }

    [Fact]
    public void ACreatorBoostDoesNotBeatAMuchFresherPost()
    {
        var fresh = Post(1);
        var oldCreator = Post(48, creator: true);

        var ranked = FeedRanking.RankForYou([oldCreator, fresh], Now, viewerHasPersona: false);

        Assert.Equal(fresh.PostId, ranked[0].PostId);
    }

    [Fact]
    public void AViewerWithAPersonaNeverSeesMatchesBelowSixtyOrPostsWithoutProducts()
    {
        var good = Post(10, match: 80);
        var borderline = Post(1, match: 60);
        var poor = Post(0, match: 59);
        var noProducts = Post(0);

        var ranked = FeedRanking.RankForYou([good, borderline, poor, noProducts], Now, viewerHasPersona: true);
        // 80 × exp(−10/48) = 0.65 beats 60 × exp(−1/48) = 0.59: a better match outweighs nine hours.
        Assert.Equal([good.PostId, borderline.PostId], ranked.Select(candidate => candidate.PostId));

        var anonymous = FeedRanking.RankForYou([good, poor, noProducts], Now, viewerHasPersona: false);
        Assert.Equal(3, anonymous.Count);
    }

    [Fact]
    public void TheMatchScalesTheScore()
    {
        var perfect = Post(5, match: 100);
        var fair = perfect with { MatchScore = 60, PostId = Guid.NewGuid() };

        Assert.Equal(0.6, FeedRanking.Score(fair, Now) / FeedRanking.Score(perfect, Now), 6);
    }

    [Fact]
    public void TiesGoToTheNewestThenTheId()
    {
        var older = Post(2, id: Guid.Parse("00000000-0000-0000-0000-000000000002"));
        var newer = Post(1, id: Guid.Parse("00000000-0000-0000-0000-000000000003")) with { LikeCount = 0 };
        var twinA = Post(3, id: Guid.Parse("00000000-0000-0000-0000-00000000000a"));
        var twinB = twinA with { PostId = Guid.Parse("00000000-0000-0000-0000-00000000000b") };

        var ranked = FeedRanking.RankForYou([twinB, older, twinA, newer], Now, false);

        Assert.Equal([newer.PostId, older.PostId, twinA.PostId, twinB.PostId], ranked.Select(candidate => candidate.PostId));
    }

    [Fact]
    public void NoTwoPostsOfOneAuthorAreAdjacentAndTheOrderIsKeptOtherwise()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        (string Name, Guid Author)[] ordered = [("a1", a), ("a2", a), ("a3", a), ("b1", b), ("c1", c), ("b2", b)];

        var result = FeedRanking.SeparateAuthors(ordered, item => item.Author);

        Assert.Equal(["a1", "b1", "a2", "c1", "a3", "b2"], result.Select(item => item.Name));
        Assert.All(result.Zip(result.Skip(1)), pair => Assert.NotEqual(pair.First.Author, pair.Second.Author));
    }

    [Fact]
    public void PostsThatCannotBePlacedAreDroppedRatherThanPlacedNextToTheirAuthor()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        (string Name, Guid Author)[] ordered = [("a1", a), ("a2", a), ("a3", a), ("b1", b)];

        Assert.Equal(["a1", "b1", "a2"], FeedRanking.SeparateAuthors(ordered, item => item.Author).Select(item => item.Name));
        Assert.Empty(FeedRanking.SeparateAuthors(Array.Empty<(string, Guid)>(), item => item.Item2));
    }

    [Fact]
    public void FollowingIsNewestFirst()
    {
        var older = Post(5) with { Reason = FeedReason.Following };
        var newer = Post(1) with { Reason = FeedReason.Following, LikeCount = 0 };

        Assert.Equal([newer.PostId, older.PostId], FeedRanking.RankFollowing([older, newer]).Select(candidate => candidate.PostId));
    }

    [Fact]
    public void TheSqlFormulaUsesTheSameConstants()
    {
        Assert.Contains("/ 48.0", FeedRanking.BaseSql, StringComparison.Ordinal);
        Assert.Contains("/ 4.0", FeedRanking.BaseSql, StringComparison.Ordinal);
        Assert.Contains("1.3", FeedRanking.BaseSql, StringComparison.Ordinal);
        Assert.Equal(48, FeedRanking.RecencyHours);
        Assert.Equal(4, FeedRanking.LikeDivisor);
    }
}

public class PostRulesTests
{
    // status, visibility, author, follows, blocked, can view
    [Theory]
    [InlineData("Live", "Public", false, false, false, true)]
    [InlineData("Live", "Followers", false, false, false, false)]
    [InlineData("Live", "Followers", false, true, false, true)]
    [InlineData("Live", "Private", false, true, false, false)]
    [InlineData("Live", "Public", false, true, true, false)]
    [InlineData("Hidden", "Public", false, false, false, false)]
    [InlineData("NeedsReview", "Public", false, false, false, false)]
    [InlineData("Draft", "Public", false, false, false, false)]
    [InlineData("Blocked", "Public", true, false, false, true)]
    [InlineData("Draft", "Private", true, false, false, true)]
    [InlineData("Deleted", "Public", true, false, false, false)]
    public void CanView(string status, string visibility, bool isAuthor, bool follows, bool blocked, bool expected) =>
        Assert.Equal(expected, PostRules.CanView(new PostViewFacts(Enum.Parse<PostStatus>(status), Enum.Parse<PostVisibility>(visibility), isAuthor, follows, blocked)));

    [Theory]
    [InlineData("Allow", "Draft")]
    [InlineData("Review", "NeedsReview")]
    [InlineData("Block", "Blocked")]
    public void ModerationDecidesTheStatusAndNeverPublishes(string verdict, string expected)
    {
        var status = PostRules.StatusAfterModeration(Enum.Parse<ModerationVerdict>(verdict));

        Assert.Equal(Enum.Parse<PostStatus>(expected), status);
        Assert.NotEqual(PostStatus.Live, status);
    }

    [Fact]
    public void TheStrictestVerdictOfSeveralImagesWins()
    {
        Assert.Equal(ModerationVerdict.Allow, PostRules.Combine([]));
        Assert.Equal(ModerationVerdict.Review, PostRules.Combine([ModerationVerdict.Allow, ModerationVerdict.Review, ModerationVerdict.Allow]));
        Assert.Equal(ModerationVerdict.Block, PostRules.Combine([ModerationVerdict.Review, ModerationVerdict.Block]));
    }

    [Fact]
    public void ConfirmationPublishesADraftButNotAPostUnderReview()
    {
        Assert.Equal(PostStatus.Live, PostRules.StatusAfterConfirmation(PostStatus.Draft));
        Assert.Equal(PostStatus.NeedsReview, PostRules.StatusAfterConfirmation(PostStatus.NeedsReview));
        Assert.Equal(PostStatus.Live, PostRules.StatusAfterConfirmation(PostStatus.Live));
        Assert.False(PostRules.CanPublish(PostStatus.Blocked));
        Assert.False(PostRules.CanPublish(PostStatus.Hidden));
    }

    [Fact]
    public void TheThirdReportHidesALivePost()
    {
        Assert.False(PostRules.ShouldHide(PostStatus.Live, 2));
        Assert.True(PostRules.ShouldHide(PostStatus.Live, 3));
        Assert.False(PostRules.ShouldHide(PostStatus.Draft, 5));
    }

    [Fact]
    public void AReviewRestoresOrBlocks()
    {
        Assert.Equal(PostStatus.Live, PostRules.StatusAfterReview(ReviewDecision.Approve, confirmed: true));
        Assert.Equal(PostStatus.Draft, PostRules.StatusAfterReview(ReviewDecision.Approve, confirmed: false));
        Assert.Equal(PostStatus.Blocked, PostRules.StatusAfterReview(ReviewDecision.Remove, confirmed: true));
        Assert.True(PostRules.IsReviewable(PostStatus.Hidden));
        Assert.False(PostRules.IsReviewable(PostStatus.Live));
    }

    [Fact]
    public void PurgeRules()
    {
        var now = new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);
        var hour = TimeSpan.FromHours(1);

        Assert.True(Kombin.Api.Features.Social.PurgeRules.IsDue(PostStatus.Deleted, now, now, now, now));
        Assert.True(Kombin.Api.Features.Social.PurgeRules.IsDue(PostStatus.Processing, null, now - TimeSpan.FromDays(1) - hour, now, now));
        Assert.False(Kombin.Api.Features.Social.PurgeRules.IsDue(PostStatus.Processing, now - TimeSpan.FromDays(2), now - TimeSpan.FromDays(2), now, now));
        Assert.True(Kombin.Api.Features.Social.PurgeRules.IsDue(PostStatus.Draft, now, now, now - TimeSpan.FromDays(7) - hour, now));
        Assert.False(Kombin.Api.Features.Social.PurgeRules.IsDue(PostStatus.Draft, now, now, now - TimeSpan.FromDays(6), now));
        Assert.True(Kombin.Api.Features.Social.PurgeRules.IsDue(PostStatus.Blocked, now, now, now - TimeSpan.FromDays(31), now));
        Assert.False(Kombin.Api.Features.Social.PurgeRules.IsDue(PostStatus.Live, now, now, now - TimeSpan.FromDays(365), now));
    }

    [Fact]
    public void TagBoxesAreClampedIntoTheImage()
    {
        Assert.Equal((0.8, 0.9, 0.2, 0.1), TagBoxes.Clamp(0.95, 0.95, 0.2, 0.1));
        Assert.Equal((0, 0, TagBoxes.MinSide, TagBoxes.MinSide), TagBoxes.Clamp(-1, double.NaN, 0, -3));
        Assert.Equal((0, 0, 1, 1), TagBoxes.Clamp(0, 0, 5, 5));
    }

    [Fact]
    public void TheCollagePutsLayersAndTopsFirstInTwoColumns()
    {
        OutfitSlot[] pieces = [OutfitSlot.Shoes, OutfitSlot.Bottom, OutfitSlot.Top, OutfitSlot.Outerwear];

        var cells = CollageLayout.Arrange(pieces, slot => slot);

        Assert.Equal([OutfitSlot.Outerwear, OutfitSlot.Top, OutfitSlot.Bottom, OutfitSlot.Shoes], cells.Select(cell => cell.Piece));
        Assert.Equal((0.02, 0.02, 0.46, 0.46), (cells[0].X, cells[0].Y, cells[0].W, cells[0].H));
        Assert.Equal((0.52, 0.52), (cells[3].X, cells[3].Y));
        Assert.All(cells, cell => Assert.True(cell.X + cell.W <= 1 && cell.Y + cell.H <= 1));
    }
}

public class PostingConsentTests
{
    /// <summary>Published texts are pinned: editing one in place fails here; add a new version instead.</summary>
    private static readonly Dictionary<string, string> PublishedHashes = new()
    {
        ["2026-11-v1"] = "930a4a54a9dd49025faef6678c66ee7c06134fb2898d5cd1ce465900178aa55f",
    };

    [Fact]
    public void PublishedTextsNeverChange()
    {
        foreach (var version in PostingConsent.Versions)
        {
            Assert.True(PublishedHashes.TryGetValue(version.Version, out var hash), $"Pin the hash of the new consent version {version.Version} here.");
            Assert.Equal(hash, version.Hash);
        }
    }

    [Fact]
    public void VersionsAreUniqueAndTheLastOneIsCurrent()
    {
        Assert.Equal(PostingConsent.Versions.Count, PostingConsent.Versions.Select(version => version.Version).Distinct().Count());
        Assert.Same(PostingConsent.Versions[^1], PostingConsent.Current);
        Assert.DoesNotContain('\r', PostingConsent.Current.Text);
        Assert.Contains("24 saat", PostingConsent.Current.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyAnAcceptanceOfTheCurrentVersionCounts()
    {
        Assert.True(PostingConsent.IsSatisfied([PostingConsent.Current.Version]));
        Assert.True(PostingConsent.IsSatisfied(["2020-01-v0", PostingConsent.Current.Version]));
        Assert.False(PostingConsent.IsSatisfied(["2020-01-v0"]));
        Assert.False(PostingConsent.IsSatisfied([]));
        Assert.Null(PostingConsent.Find("nope"));
    }
}

public class CreatorEarningsTests
{
    [Fact]
    public void TheEstimateMultipliesClicksConversionBasketCommissionAndShare()
    {
        var options = new CreatorOptions { SharePercent = 40, AverageOrderValue = 750, CommissionPercent = 8, ConversionRatePercent = 3 };

        var estimate = CreatorEarnings.Estimate(100, options);

        // 100 clicks × 3 % × 750 ₺ × 8 % × 40 % = 72 ₺
        Assert.Equal(72m, estimate.Amount);
        Assert.True(estimate.IsEstimate);
        Assert.Equal(("TRY", 100, 40m), (estimate.Currency, estimate.Clicks, estimate.SharePercent));
    }

    [Fact]
    public void AConversionRateOfOneHundredIsTheBriefsLiteralFormula()
    {
        var options = new CreatorOptions { ConversionRatePercent = 100 };

        Assert.Equal(10 * 750m * 0.08m * 0.40m, CreatorEarnings.Estimate(10, options).Amount);
    }

    [Fact]
    public void NoClicksNoEarningsAndRoundingIsToKurus()
    {
        Assert.Equal(0m, CreatorEarnings.Estimate(0, new CreatorOptions()).Amount);
        Assert.Equal(0m, CreatorEarnings.Estimate(-3, new CreatorOptions()).Amount);
        Assert.Equal(0.72m, CreatorEarnings.Estimate(1, new CreatorOptions()).Amount);
        Assert.Equal(33.33m, CreatorEarnings.CreatorShare(83.33m, 40));
    }

    [Fact]
    public void OptionsOutsideTheirRangesAreRejected()
    {
        var validator = new CreatorOptionsValidator();

        Assert.True(validator.Validate(null, new CreatorOptions()).Succeeded);
        Assert.True(validator.Validate(null, new CreatorOptions { SharePercent = 120 }).Failed);
        Assert.True(validator.Validate(null, new CreatorOptions { AverageOrderValue = -1 }).Failed);
    }
}

public class ImageMetadataTests
{
    private static byte[] Segment(byte marker, byte[] data) =>
        [0xFF, marker, (byte)((data.Length + 2) >> 8), (byte)((data.Length + 2) & 0xFF), .. data];

    private static byte[] Tiff(bool littleEndian, ushort orientation)
    {
        var tiff = new byte[26];
        tiff[0] = tiff[1] = (byte)(littleEndian ? 'I' : 'M');
        if (littleEndian)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(2), 42);
            BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(4), 8);
            BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(8), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(10), 0x0112);
            BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(12), 3);
            BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(14), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(18), orientation);
        }
        else
        {
            BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(2), 42);
            BinaryPrimitives.WriteUInt32BigEndian(tiff.AsSpan(4), 8);
            BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(8), 1);
            BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(10), 0x0112);
            BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(12), 3);
            BinaryPrimitives.WriteUInt32BigEndian(tiff.AsSpan(14), 1);
            BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(18), orientation);
        }

        return tiff;
    }

    private static readonly byte[] Jfif = Segment(0xE0, [.. "JFIF\0"u8, 1, 1, 0, 0, 1, 0, 1, 0, 0]);

    private static readonly byte[] Icc = Segment(0xE2, [.. "ICC_PROFILE\0"u8, 1, 1, 0xAA, 0xBB]);

    private static readonly byte[] Quantization = Segment(0xDB, [0, 1, 2, 3, 4]);

    private static readonly byte[] Scan = [.. Segment(0xDA, [1, 1, 0, 0, 0x3F, 0]), 0x12, 0x34, 0xFF, 0x00, 0x56, 0xFF, 0xD9];

    private static byte[] Jpeg(ushort orientation, bool littleEndian = false) =>
    [
        0xFF, 0xD8,
        .. Jfif,
        .. Segment(0xE1, [.. "Exif\0\0"u8, .. Tiff(littleEndian, orientation), .. "GPS 41.0082 N 28.9784 E"u8]),
        .. Segment(0xE1, [.. "http://ns.adobe.com/xap/1.0/\0"u8, .. "<x:xmpmeta>location</x:xmpmeta>"u8]),
        .. Icc,
        .. Segment(0xE2, [.. "MPF\0"u8, 9, 9, 9]),
        .. Segment(0xED, [.. "Photoshop 3.0\0"u8, .. "IPTC city"u8]),
        .. Segment(0xFE, "shot at home"u8.ToArray()),
        .. Quantization,
        .. Scan,
    ];

    [Fact]
    public void AJpegLosesEveryMetadataSegmentAndKeepsItsPixelsAndColourProfile()
    {
        var stripped = ImageMetadata.Strip(Jpeg(orientation: 1))!;
        var text = Encoding.Latin1.GetString(stripped.Content);

        Assert.Equal("image/jpeg", stripped.ContentType);
        Assert.Null(stripped.Orientation);
        Assert.Equal(5, stripped.RemovedSegments);
        Assert.DoesNotContain("GPS", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Exif", text, StringComparison.Ordinal);
        Assert.DoesNotContain("xmpmeta", text, StringComparison.Ordinal);
        Assert.DoesNotContain("IPTC", text, StringComparison.Ordinal);
        Assert.DoesNotContain("MPF", text, StringComparison.Ordinal);
        Assert.DoesNotContain("shot at home", text, StringComparison.Ordinal);
        Assert.Equal([0xFF, 0xD8, .. Jfif, .. Icc, .. Quantization, .. Scan], stripped.Content);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ARotatedPhotoKeepsOnlyItsOrientation(bool littleEndian)
    {
        var stripped = ImageMetadata.Strip(Jpeg(orientation: 6, littleEndian))!;

        Assert.Equal(6, stripped.Orientation);
        Assert.Equal([0xFF, 0xD8, .. Jfif, .. ImageMetadata.OrientationSegment(6), .. Icc, .. Quantization, .. Scan], stripped.Content);
        Assert.DoesNotContain("GPS", Encoding.Latin1.GetString(stripped.Content), StringComparison.Ordinal);
    }

    [Fact]
    public void StrippingIsIdempotent()
    {
        var once = ImageMetadata.Strip(Jpeg(orientation: 8))!;
        var twice = ImageMetadata.Strip(once.Content)!;

        Assert.Equal(once.Content, twice.Content);
        Assert.Equal(8, twice.Orientation);
    }

    [Fact]
    public void APngLosesItsTextExifAndTimeChunks()
    {
        static byte[] Chunk(string type, byte[] data)
        {
            var chunk = new byte[12 + data.Length];
            BinaryPrimitives.WriteUInt32BigEndian(chunk, (uint)data.Length);
            Encoding.ASCII.GetBytes(type).CopyTo(chunk, 4);
            data.CopyTo(chunk, 8);
            return chunk;
        }

        byte[] signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        var header = Chunk("IHDR", new byte[13]);
        var data = Chunk("IDAT", [1, 2, 3]);
        var end = Chunk("IEND", []);
        byte[] png = [.. signature, .. header, .. Chunk("tEXt", "Location\0Istanbul"u8.ToArray()), .. Chunk("eXIf", [1, 2]), .. Chunk("tIME", new byte[7]), .. data, .. end];

        var stripped = ImageMetadata.Strip(png)!;

        Assert.Equal("image/png", stripped.ContentType);
        Assert.Equal(3, stripped.RemovedSegments);
        Assert.Equal([.. signature, .. header, .. data, .. end], stripped.Content);
    }

    [Fact]
    public void AWebpLosesItsExifAndXmpChunksAndTheirFlags()
    {
        static byte[] Chunk(string type, byte[] data)
        {
            var padded = data.Length % 2 == 1;
            var chunk = new byte[8 + data.Length + (padded ? 1 : 0)];
            Encoding.ASCII.GetBytes(type).CopyTo(chunk, 0);
            BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(4), (uint)data.Length);
            data.CopyTo(chunk, 8);
            return chunk;
        }

        var vp8x = Chunk("VP8X", [0x0C, 0, 0, 0, 1, 0, 0, 1, 0, 0]);
        var image = Chunk("VP8 ", [9, 8, 7]);
        byte[] body = [.. "WEBP"u8, .. vp8x, .. image, .. Chunk("EXIF", "GPS"u8.ToArray()), .. Chunk("XMP ", "<xmp/>"u8.ToArray())];
        byte[] webp = [.. "RIFF"u8, 0, 0, 0, 0, .. body];
        BinaryPrimitives.WriteUInt32LittleEndian(webp.AsSpan(4), (uint)body.Length);

        var stripped = ImageMetadata.Strip(webp)!;

        Assert.Equal(("image/webp", 2), (stripped.ContentType, stripped.RemovedSegments));
        Assert.Equal(0, stripped.Content[20] & 0x0C);
        Assert.Equal((uint)(stripped.Content.Length - 8), BinaryPrimitives.ReadUInt32LittleEndian(stripped.Content.AsSpan(4)));
        Assert.DoesNotContain("GPS", Encoding.Latin1.GetString(stripped.Content), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not an image")]
    [InlineData("GIF89a....")]
    public void AnythingElseIsRefused(string content) => Assert.Null(ImageMetadata.Strip(Encoding.ASCII.GetBytes(content)));

    [Fact]
    public void ATruncatedJpegIsRefusedRatherThanPassedThrough()
    {
        var jpeg = Jpeg(orientation: 1);

        Assert.Null(ImageMetadata.Strip(jpeg[..30]));
    }
}

public class SocialAnalyticsTests
{
    private static Dictionary<string, JsonElement> Props(object value) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(value))!;

    [Fact]
    public void TheSocialEventsAreAllowListed()
    {
        var post = Guid.NewGuid();

        Assert.Null(AnalyticsEventCatalog.Reject("post_view", Props(new { postId = post, source = "feed", position = 3 })));
        Assert.Null(AnalyticsEventCatalog.Reject("post_tag_tap", Props(new { postId = post, tagKind = "Product", hasOffer = true })));
        Assert.Null(AnalyticsEventCatalog.Reject("post_like", Props(new { postId = post, liked = false })));
        Assert.Null(AnalyticsEventCatalog.Reject("post_create_start", Props(new { kind = "Photo", source = "camera" })));
        Assert.Null(AnalyticsEventCatalog.Reject("post_create_done", Props(new { kind = "Outfit", status = "Live", tagCount = 4, visibility = "Public", durationMs = 41000 })));
        Assert.Null(AnalyticsEventCatalog.Reject("post_report", Props(new { postId = post, reason = "Spam" })));
        Assert.Null(AnalyticsEventCatalog.Reject("creator_profile_view", Props(new { creatorId = post })));
        Assert.Null(AnalyticsEventCatalog.Reject("try_with_wardrobe", Props(new { postId = post, anchorCount = 2, outfitCount = 4 })));
    }

    [Fact]
    public void CaptionsAndHandlesNeverGetIn()
    {
        var post = Guid.NewGuid();

        Assert.Equal(RejectionReasons.InvalidProperties, AnalyticsEventCatalog.Reject("post_view", Props(new { postId = post, source = "feed", caption = "Cuma kombini" })));
        Assert.Equal(RejectionReasons.InvalidProperties, AnalyticsEventCatalog.Reject("creator_profile_view", Props(new { creatorId = post, handle = "ayse" })));
        Assert.Equal(RejectionReasons.InvalidProperties, AnalyticsEventCatalog.Reject("post_create_done", Props(new { kind = "Photo", status = "Posted", tagCount = 1, durationMs = 5 })));
    }
}
