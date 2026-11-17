using Kombin.Api.Common.Imaging;
using Kombin.Api.Features.TryOn;
using Kombin.Api.Features.TryOn.Rules;
using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Profile;
using Kombin.Api.Persistence.Entities.TryOn;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace Kombin.Api.Tests;

/// <summary>"Manken üstünde gör": the readiness rule case by case, the body, the prepare rules, the cache key, the render gate and the downscaling.</summary>
public sealed class TryOnRulesTests
{
    private const double MaxBmi = 36;

    private static TryOnPieceFacts Ready(TryOnFamily family, bool? mayBeOutermost = null) =>
        new(Guid.NewGuid(), family, TryOnPieceState.Prepared, true, family == TryOnFamily.Top ? mayBeOutermost ?? true : mayBeOutermost);

    private static TryOnPieceFacts In(TryOnFamily family, TryOnPieceState state, bool reliable = false) => new(Guid.NewGuid(), family, state, reliable);

    private static TryOnDecision Decide(params TryOnPieceFacts[] pieces) => TryOnReadiness.Decide(pieces, 22, MaxBmi);

    // --- Readiness ------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TopBottomAndShoesAllReliableAreReady()
    {
        var decision = Decide(Ready(TryOnFamily.Top), Ready(TryOnFamily.Trousers), Ready(TryOnFamily.Shoes));

        Assert.Equal((TryOnStatus.Ready, (TryOnUnavailableReason?)null), (decision.Status, decision.Reason));
        Assert.Empty(decision.Prepare);
    }

    [Fact]
    public void ShoesAreOptionalAndASkirtIsABottomAndOuterwearIsATop()
    {
        Assert.Equal(TryOnStatus.Ready, Decide(Ready(TryOnFamily.Top), Ready(TryOnFamily.Skirt)).Status);
        Assert.Equal(TryOnStatus.Ready, Decide(Ready(TryOnFamily.Outerwear), Ready(TryOnFamily.Trousers)).Status);
    }

    [Fact]
    public void ABmiOverTheLimitIsUnavailableWhateverTheOutfit()
    {
        var decision = TryOnReadiness.Decide([Ready(TryOnFamily.Top), Ready(TryOnFamily.Trousers)], 36.01, MaxBmi);

        Assert.Equal((TryOnStatus.Unavailable, TryOnUnavailableReason.BodyOutOfRange), (decision.Status, decision.Reason));
        Assert.Equal(TryOnStatus.Ready, TryOnReadiness.Decide([Ready(TryOnFamily.Top), Ready(TryOnFamily.Trousers)], 36, MaxBmi).Status);
    }

    [Fact]
    public void NoTopOrNoBottomIsUnavailableNeverBareLegs()
    {
        Assert.Equal(TryOnUnavailableReason.NoBottom, Decide(Ready(TryOnFamily.Top), Ready(TryOnFamily.Shoes)).Reason);
        Assert.Equal(TryOnUnavailableReason.NoTop, Decide(Ready(TryOnFamily.Trousers), Ready(TryOnFamily.Shoes)).Reason);
        Assert.Equal(TryOnUnavailableReason.NoBottom, Decide(In(TryOnFamily.Top, TryOnPieceState.NoRow)).Reason);
    }

    [Fact]
    public void APieceWithoutAFamilyIsUnavailable() =>
        Assert.Equal(TryOnUnavailableReason.UnknownCategory, Decide(Ready(TryOnFamily.Top), Ready(TryOnFamily.Trousers), new TryOnPieceFacts(Guid.NewGuid(), null, TryOnPieceState.NoRow)).Reason);

    [Fact]
    public void ACorePiecePhotographedOnAPersonIsUnavailable() =>
        Assert.Equal(TryOnUnavailableReason.PersonInPhoto, Decide(In(TryOnFamily.Top, TryOnPieceState.PersonInPhoto), Ready(TryOnFamily.Trousers)).Reason);

    [Theory]
    [InlineData("Top")]
    [InlineData("Outerwear")]
    [InlineData("Trousers")]
    [InlineData("Shoes")]
    public void AnUnreliableRefusedOrFailedCorePieceIsUnavailable(string name)
    {
        var family = Enum.Parse<TryOnFamily>(name);
        TryOnPieceFacts[] others = family switch
        {
            TryOnFamily.Trousers => [Ready(TryOnFamily.Top)],
            TryOnFamily.Top or TryOnFamily.Outerwear => [Ready(TryOnFamily.Trousers)],
            _ => [Ready(TryOnFamily.Top), Ready(TryOnFamily.Trousers)],
        };

        Assert.Equal(TryOnUnavailableReason.PieceNotReady, Decide([.. others, In(family, TryOnPieceState.Prepared, reliable: false)]).Reason);
        Assert.Equal(TryOnUnavailableReason.PieceNotReady, Decide([.. others, In(family, TryOnPieceState.NotReady)]).Reason);
    }

    [Fact]
    public void ATopWithFoldedSleevesMayNotBeTheOutermostLayer() =>
        Assert.Equal(TryOnUnavailableReason.FoldedSleeves, Decide(Ready(TryOnFamily.Top, mayBeOutermost: false), Ready(TryOnFamily.Trousers)).Reason);

    [Fact]
    public void ATopWithFoldedSleevesUnderOuterwearIsFineAndOuterwearIsNeverRestricted()
    {
        Assert.Equal(TryOnStatus.Ready, Decide(Ready(TryOnFamily.Top, mayBeOutermost: false), Ready(TryOnFamily.Outerwear), Ready(TryOnFamily.Trousers)).Status);
        Assert.Equal(TryOnStatus.Ready, Decide(Ready(TryOnFamily.Outerwear, mayBeOutermost: false), Ready(TryOnFamily.Trousers)).Status);
    }

    [Fact]
    public void BagsAndBeltsNeverBlockButAreAskedFor()
    {
        var bag = In(TryOnFamily.Bag, TryOnPieceState.Prepared, reliable: false);
        var belt = In(TryOnFamily.Belt, TryOnPieceState.NoRow);
        var person = In(TryOnFamily.Bag, TryOnPieceState.PersonInPhoto);

        var decision = Decide(Ready(TryOnFamily.Top), Ready(TryOnFamily.Trousers), bag, belt, person);

        Assert.Equal(TryOnStatus.Ready, decision.Status);
        Assert.Equal([belt.PieceId], decision.Prepare);
        Assert.Equal(TryOnUnavailableReason.PieceNotReady, Decide(Ready(TryOnFamily.Top), Ready(TryOnFamily.Trousers), In(TryOnFamily.Shoes, TryOnPieceState.NotReady), bag).Reason);
    }

    [Fact]
    public void ACorePieceWithoutAnAnswerIsPreparingAndIsAskedFor()
    {
        var missing = In(TryOnFamily.Trousers, TryOnPieceState.NoRow);
        var pending = In(TryOnFamily.Shoes, TryOnPieceState.Pending);

        var decision = Decide(Ready(TryOnFamily.Top), missing, pending);

        Assert.Equal((TryOnStatus.Preparing, (TryOnUnavailableReason?)null), (decision.Status, decision.Reason));
        Assert.Equal([missing.PieceId, pending.PieceId], decision.Prepare);
    }

    [Fact]
    public void UnavailableWinsOverPreparingAndTheFoldedTopWaitsForItsAnswer()
    {
        Assert.Equal(TryOnStatus.Unavailable, Decide(In(TryOnFamily.Top, TryOnPieceState.NoRow), In(TryOnFamily.Trousers, TryOnPieceState.Prepared, reliable: false)).Status);
        Assert.Empty(Decide(In(TryOnFamily.Top, TryOnPieceState.NoRow), In(TryOnFamily.Trousers, TryOnPieceState.NotReady)).Prepare);
        Assert.Equal(TryOnStatus.Preparing, Decide(In(TryOnFamily.Top, TryOnPieceState.Pending), Ready(TryOnFamily.Trousers)).Status);
    }

    [Fact]
    public void FamiliesFollowTheVisionServicesCategories()
    {
        Assert.Equal(TryOnFamily.Outerwear, TryOnFamilies.ForCategory("ust-giyim/blazer"));
        Assert.Equal(TryOnFamily.Top, TryOnFamilies.ForCategory("ust-giyim/triko"));
        Assert.Equal(TryOnFamily.Skirt, TryOnFamilies.ForCategory("alt-giyim/etek"));
        Assert.Equal(TryOnFamily.Belt, TryOnFamilies.ForCategory("aksesuar/kemer"));
        Assert.Null(TryOnFamilies.ForCategory("alt-giyim"));
        Assert.Null(TryOnFamilies.ForCategory(null));
        Assert.Equal(TryOnFamily.Trousers, TryOnFamilies.Parse("trousers"));
        Assert.False(TryOnFamilies.IsCore(TryOnFamily.Bag));
        Assert.True(TryOnFamilies.IsCore(TryOnFamily.Shoes));
    }

    // --- The body ---------------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Women", 22)]
    [InlineData("Men", 24)]
    [InlineData("Unisex", 23)]
    public void WithoutAWeightTheBmiIsTheAudiencesDefault(string audience, double bmi)
    {
        var body = TryOnBody.From(new UserProfile { Audience = Enum.Parse<Audience>(audience), HeightCm = 150 });

        Assert.Equal(bmi, body.Bmi);
        Assert.True(TryOnReadiness.Decide([Ready(TryOnFamily.Top), Ready(TryOnFamily.Trousers)], body.Bmi, MaxBmi).Status == TryOnStatus.Ready);
    }

    [Fact]
    public void TheBmiUsesTheAudiencesDefaultHeightWhenThereIsNone()
    {
        Assert.Equal(98 / (1.65 * 1.65), TryOnBody.From(new UserProfile { Audience = Audience.Women, WeightKg = 98 }).Bmi, 6);
        Assert.Equal(100 / (1.77 * 1.77), TryOnBody.From(new UserProfile { Audience = Audience.Men, WeightKg = 100 }).Bmi, 6);
        Assert.Equal(120 / (1.75 * 1.75), TryOnBody.From(new UserProfile { Audience = Audience.Men, WeightKg = 120, HeightCm = 175 }).Bmi, 6);
        Assert.Equal(23, TryOnBody.From(null).Bmi);
        Assert.Equal("unisex", TryOnBody.From(null).ServiceAudience);
        Assert.Equal("unisex", TryOnBody.From(new UserProfile { Audience = Audience.Kids }).ServiceAudience);
    }

    [Fact]
    public void TheBodyHashChangesWithEveryValue()
    {
        var body = new TryOnBody(Audience.Women, 165, 60m, 88, 70, 96);
        var hashes = new[]
        {
            body.Hash(), (body with { Audience = Audience.Men }).Hash(), (body with { HeightCm = 166 }).Hash(), (body with { WeightKg = 61m }).Hash(),
            (body with { ChestCm = null }).Hash(), (body with { WaistCm = 71 }).Hash(), (body with { HipCm = 97 }).Hash(),
        };

        Assert.Equal(hashes.Length, hashes.Distinct().Count());
        Assert.Equal(body.Hash(), new TryOnBody(Audience.Women, 165, 60m, 88, 70, 96).Hash());
    }

    // --- Preparing --------------------------------------------------------------------------------------------------------------------------

    private static readonly DateTimeOffset Now = new(2026, 11, 18, 9, 0, 0, TimeSpan.Zero);

    private static GarmentRowState Row(
        TryOnGarmentStatus status, string source = "s", string? prepared = "s", string? version = "v6", int attempts = 0, DateTimeOffset? next = null, DateTimeOffset? lease = null, DateTimeOffset? requested = null) =>
        new(status, source, prepared, version, attempts, next, lease, requested);

    [Fact]
    public void WhatIsDueAndInWhichOrder()
    {
        Assert.Equal(PrepareUrgency.New, TryOnPrepareRules.Due(null, "s", "v6", Now));
        Assert.Equal(PrepareUrgency.Requested, TryOnPrepareRules.Due(Row(TryOnGarmentStatus.Pending, source: "", prepared: null, version: null, requested: Now), "s", "v6", Now));
        Assert.Equal(PrepareUrgency.SourceChanged, TryOnPrepareRules.Due(Row(TryOnGarmentStatus.Prepared), "t", "v6", Now));
        Assert.Equal(PrepareUrgency.SourceChanged, TryOnPrepareRules.Due(Row(TryOnGarmentStatus.Refused, prepared: null), "t", "v6", Now));
        Assert.Equal(PrepareUrgency.Version, TryOnPrepareRules.Due(Row(TryOnGarmentStatus.Prepared, version: "v5"), "s", "v6", Now));
        Assert.Equal(PrepareUrgency.None, TryOnPrepareRules.Due(Row(TryOnGarmentStatus.Prepared), "s", "v6", Now));
        Assert.Equal(PrepareUrgency.None, TryOnPrepareRules.Due(Row(TryOnGarmentStatus.Prepared, version: "v5"), "s", null, Now));
        Assert.Equal(PrepareUrgency.None, TryOnPrepareRules.Due(Row(TryOnGarmentStatus.Refused, prepared: null), "s", "v6", Now));
        Assert.True(PrepareUrgency.Requested < PrepareUrgency.New && PrepareUrgency.New < PrepareUrgency.SourceChanged
            && PrepareUrgency.SourceChanged < PrepareUrgency.Retry && PrepareUrgency.Retry < PrepareUrgency.Version);
    }

    [Fact]
    public void NothingIsDueWhileAnotherPrepareHoldsTheRowOrWhileItsBackOffRuns()
    {
        Assert.Equal(PrepareUrgency.None, TryOnPrepareRules.Due(Row(TryOnGarmentStatus.Pending, prepared: null, lease: Now.AddMinutes(1)), "s", "v6", Now));
        Assert.Equal(PrepareUrgency.None, TryOnPrepareRules.Due(Row(TryOnGarmentStatus.Pending, prepared: null, attempts: 1, next: Now.AddMinutes(1)), "s", "v6", Now));
        Assert.Equal(PrepareUrgency.Retry, TryOnPrepareRules.Due(Row(TryOnGarmentStatus.Pending, prepared: null, attempts: 1, next: Now), "s", "v6", Now));
        Assert.Equal(PrepareUrgency.None, TryOnPrepareRules.Due(Row(TryOnGarmentStatus.Failed, prepared: null, next: Now.AddHours(1)), "s", "v6", Now));
        Assert.Equal(PrepareUrgency.Retry, TryOnPrepareRules.Due(Row(TryOnGarmentStatus.Failed, prepared: null, next: Now.AddSeconds(-1)), "s", "v6", Now));

        // An answered row whose new source failed keeps its answer and waits; then its new source is tried again.
        Assert.Equal(PrepareUrgency.None, TryOnPrepareRules.Due(Row(TryOnGarmentStatus.Prepared, source: "t", prepared: "s", next: Now.AddMinutes(5)), "t", "v6", Now));
        Assert.Equal(PrepareUrgency.Retry, TryOnPrepareRules.Due(Row(TryOnGarmentStatus.Prepared, source: "t", prepared: "s", next: Now.AddMinutes(-5)), "t", "v6", Now));
        Assert.Equal(PrepareUrgency.None, TryOnPrepareRules.Due(Row(TryOnGarmentStatus.Prepared, version: "v5", next: Now.AddMinutes(5)), "s", "v6", Now));
    }

    [Fact]
    public void ATransientFailureIsTriedOnceMoreLaterThenGivenUpForADay()
    {
        var (retry, day) = (TimeSpan.FromMinutes(10), TimeSpan.FromHours(24));

        Assert.Equal(new FailureOutcome(TryOnGarmentStatus.Pending, 1, Now + retry), TryOnPrepareRules.AfterFailure(false, 0, true, Now, retry, day));
        Assert.Equal(new FailureOutcome(TryOnGarmentStatus.Failed, 0, Now + day), TryOnPrepareRules.AfterFailure(false, 1, true, Now, retry, day));
        Assert.Equal(new FailureOutcome(TryOnGarmentStatus.Prepared, 1, Now + retry), TryOnPrepareRules.AfterFailure(true, 0, true, Now, retry, day));
        Assert.Equal(new FailureOutcome(TryOnGarmentStatus.Prepared, 0, Now + day), TryOnPrepareRules.AfterFailure(true, 1, true, Now, retry, day));
    }

    [Fact]
    public void A4xxIsNeverRetriedUntilTheSourceChanges()
    {
        var (retry, day) = (TimeSpan.FromMinutes(10), TimeSpan.FromHours(24));

        Assert.Equal(new FailureOutcome(TryOnGarmentStatus.Refused, 0, null), TryOnPrepareRules.AfterFailure(false, 0, false, Now, retry, day));
        Assert.Equal(new FailureOutcome(TryOnGarmentStatus.Prepared, 0, Now + day), TryOnPrepareRules.AfterFailure(true, 0, false, Now, retry, day));
    }

    [Fact]
    public void TheNightWindowMayWrapMidnight()
    {
        var (one, seven) = (new TimeOnly(1, 0), new TimeOnly(7, 0));
        Assert.True(TryOnPrepareRules.IsNight(new TimeOnly(3, 0), one, seven));
        Assert.False(TryOnPrepareRules.IsNight(new TimeOnly(7, 0), one, seven));
        Assert.False(TryOnPrepareRules.IsNight(new TimeOnly(12, 0), one, seven));
        Assert.True(TryOnPrepareRules.IsNight(new TimeOnly(23, 30), new TimeOnly(23, 0), seven));
        Assert.True(TryOnPrepareRules.IsNight(new TimeOnly(2, 0), new TimeOnly(23, 0), seven));
    }

    [Fact]
    public void TheSourceHashFollowsTheGalleryAndTheCategory()
    {
        Assert.NotEqual(TryOnSources.Product("ust-giyim/tisort", ["a", "b"]), TryOnSources.Product("ust-giyim/tisort", ["b", "a"]));
        Assert.NotEqual(TryOnSources.Product("ust-giyim/tisort", ["a"]), TryOnSources.Product("ust-giyim/gomlek", ["a"]));
        Assert.Equal(TryOnSources.Product("x", ["1", "2", "3", "4", "5", "6"]), TryOnSources.Product("x", ["1", "2", "3", "4", "5", "6", "7"]));
        Assert.NotEqual(TryOnSources.Wardrobe("ust-giyim/tisort", "k"), TryOnSources.Wardrobe("ust-giyim/gomlek", "k"));
        Assert.Matches("^[0-9a-f]{64}$", TryOnSources.Wardrobe("ust-giyim/tisort", "k"));
    }

    // --- The cache key ----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void EveryInputOfTheDrawingChangesTheCacheKey()
    {
        var body = new TryOnBody(Audience.Women, 165, 60m, null, 70, null);
        var garment = new CacheKeyGarment(Guid.NewGuid(), "prepare-v6", Now, "hash", "M", "M", "Regular");
        var key = TryOnCacheKey.For("mannequin-v5", body, [garment], 600);

        Assert.Equal(key, TryOnCacheKey.For("mannequin-v5", body with { }, [garment with { }], 600));
        var changed = new[]
        {
            TryOnCacheKey.For("mannequin-v6", body, [garment], 600),
            TryOnCacheKey.For("mannequin-v5", body with { WeightKg = 61m }, [garment], 600),
            TryOnCacheKey.For("mannequin-v5", body with { Audience = Audience.Unisex }, [garment], 600),
            TryOnCacheKey.For("mannequin-v5", body, [garment with { GarmentId = Guid.NewGuid() }], 600),
            TryOnCacheKey.For("mannequin-v5", body, [garment with { Version = "prepare-v7" }], 600),
            TryOnCacheKey.For("mannequin-v5", body, [garment with { PreparedAt = Now.AddSeconds(1) }], 600),
            TryOnCacheKey.For("mannequin-v5", body, [garment with { PreparedHash = "other" }], 600),
            TryOnCacheKey.For("mannequin-v5", body, [garment with { Size = "L" }], 600),
            TryOnCacheKey.For("mannequin-v5", body, [garment with { ReferenceSize = "S" }], 600),
            TryOnCacheKey.For("mannequin-v5", body, [garment with { Cut = "Slim" }], 600),
            TryOnCacheKey.For("mannequin-v5", body, [garment], 900),
            TryOnCacheKey.For("mannequin-v5", body, [garment, garment with { GarmentId = Guid.NewGuid() }], 600),
        };

        Assert.DoesNotContain(key, changed);
        Assert.Equal(changed.Length, changed.Distinct().Count());
    }

    // --- The render gate --------------------------------------------------------------------------------------------------------------------

    private sealed class Monitor(TryOnOptions value) : IOptionsMonitor<TryOnOptions>
    {
        public TryOnOptions CurrentValue => value;

        public TryOnOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<TryOnOptions, string?> listener) => null;
    }

    [Fact]
    public void TheGateAllowsOneRenderInFlightAndALimitPerRollingHour()
    {
        var gate = new TryOnRenderGate(new Monitor(new TryOnOptions { RendersPerHour = 3 }));
        var user = Guid.NewGuid();

        Assert.Equal(RenderGateVerdict.Started, gate.TryStart(user, Now).Verdict);
        Assert.Equal(RenderGateVerdict.InFlight, gate.TryStart(user, Now).Verdict);
        Assert.Equal(RenderGateVerdict.Started, gate.TryStart(Guid.NewGuid(), Now).Verdict);
        gate.Finish(user);

        Assert.Equal(RenderGateVerdict.Started, gate.TryStart(user, Now.AddMinutes(10)).Verdict);
        gate.Finish(user);
        Assert.Equal(RenderGateVerdict.Started, gate.TryStart(user, Now.AddMinutes(20)).Verdict);
        gate.Finish(user);

        var limited = gate.TryStart(user, Now.AddMinutes(30));
        Assert.Equal((RenderGateVerdict.OverLimit, TimeSpan.FromMinutes(30)), (limited.Verdict, limited.RetryAfter));

        // The first start leaves the window after an hour.
        Assert.Equal(RenderGateVerdict.Started, gate.TryStart(user, Now.AddHours(1)).Verdict);
    }

    [Fact]
    public void TheDefaultsAreValid() =>
        Assert.True(new TryOnOptionsValidator().Validate(null, new TryOnOptions()).Succeeded);

    [Fact]
    public void ImpossibleSettingsAreAllListed()
    {
        var result = new TryOnOptionsValidator().Validate(null, new TryOnOptions { RenderWidth = 100, RendersPerHour = 0, MaxBmi = 0 });

        Assert.True(result.Failed);
        Assert.Equal(3, result.Failures!.Count());
    }

    // --- Downscaling ------------------------------------------------------------------------------------------------------------------------

    /// <summary>A PNG of noise (it does not compress) with a transparent border, about <paramref name="width"/> × <paramref name="height"/> × 4 bytes.</summary>
    private static byte[] NoisyPng(int width, int height, bool transparent)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        var pixels = bitmap.GetPixelSpan();
        var random = new Random(42);
        random.NextBytes(pixels);
        for (var offset = 3; offset < pixels.Length; offset += 4)
        {
            var x = (offset / 4) % width;
            pixels[offset] = transparent && (x < width / 10 || x > width - (width / 10)) ? (byte)0 : (byte)255;
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);

        return data.ToArray();
    }

    [Fact]
    public void ATwentyMegabyteTransparentPngBecomesAtMostEightMegabytesAt1600PixelsAndKeepsItsAlpha()
    {
        var source = NoisyPng(2800, 2100, transparent: true);
        Assert.InRange(source.LongLength, 19L * 1024 * 1024, 30L * 1024 * 1024);

        var fitted = Assert.Single(VisionImageFit.FitRequest([(source, "image/png")], 8L * 1024 * 1024, 16L * 1024 * 1024, 1600));

        Assert.True(fitted.Scaled);
        Assert.Equal("image/png", fitted.ContentType);
        Assert.True(fitted.Content.LongLength <= 8L * 1024 * 1024, $"{fitted.Content.LongLength} bytes");
        using var decoded = SKBitmap.Decode(fitted.Content);
        Assert.True(Math.Max(decoded.Width, decoded.Height) <= 1600);
        Assert.Equal(1600, decoded.Width);
        Assert.Equal(0, decoded.GetPixel(5, decoded.Height / 2).Alpha);
        Assert.Equal(255, decoded.GetPixel(decoded.Width / 2, decoded.Height / 2).Alpha);
    }

    [Fact]
    public void AnOpaquePhotoIsScaledToAJpegAndASmallOneIsSentAsItIs()
    {
        var opaque = NoisyPng(2600, 2000, transparent: false);
        var small = NoisyPng(200, 300, transparent: true);

        var fitted = VisionImageFit.FitRequest([(small, "image/png"), (opaque, "image/png")], 8L * 1024 * 1024, 16L * 1024 * 1024, 1600);

        Assert.Equal(2, fitted.Count);
        Assert.Equal((0, false, "image/png"), (fitted[0].SourceIndex, fitted[0].Scaled, fitted[0].ContentType));
        Assert.Same(small, fitted[0].Content);
        Assert.Equal((1, true, "image/jpeg"), (fitted[1].SourceIndex, fitted[1].Scaled, fitted[1].ContentType));
        using var decoded = SKBitmap.Decode(fitted[1].Content);
        Assert.Equal(1600, Math.Max(decoded.Width, decoded.Height));
    }

    [Fact]
    public void ARequestOverSixteenMegabytesScalesItsLargestPhotosUntilItFits()
    {
        // Three photos of ~6 MB each: none over the per-photo limit, 18 MB together.
        var photo = NoisyPng(1300, 1350, transparent: true);
        Assert.InRange(photo.LongLength, 5.5 * 1024 * 1024, 8L * 1024 * 1024);

        var fitted = VisionImageFit.FitRequest([(photo, "image/png"), (photo, "image/png"), (photo, "image/png")], 8L * 1024 * 1024, 16L * 1024 * 1024, 1000);

        Assert.Equal(3, fitted.Count);
        Assert.True(fitted.Sum(item => item.Content.LongLength) <= 16L * 1024 * 1024);
        Assert.Equal([0, 1, 2], fitted.Select(item => item.SourceIndex));
    }

    [Fact]
    public void APhotoTooLargeInPixelsOrNotAnImageIsNeverDecodedAndLeftOut()
    {
        var big = NoisyPng(2600, 2000, transparent: true);
        var notAnImage = new byte[9 * 1024 * 1024];

        Assert.Null(VisionImageFit.Scale(big, 1600, 8L * 1024 * 1024, maxPixels: 1_000_000));
        Assert.Null(VisionImageFit.Scale(notAnImage, 1600, 8L * 1024 * 1024));
        Assert.Empty(VisionImageFit.FitRequest([(notAnImage, "image/png")], 8L * 1024 * 1024, 16L * 1024 * 1024, 1600));
    }
}
