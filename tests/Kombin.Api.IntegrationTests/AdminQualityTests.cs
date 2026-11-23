using System.Net;
using Kombin.Api.Features.Admin;
using Kombin.Api.Persistence.Entities.Recommendation;
using Microsoft.EntityFrameworkCore;

namespace Kombin.Api.IntegrationTests;

/// <summary>
/// The admin "Öneri kalitesi" page (<c>/admin/quality</c>) and <see cref="SuggestionQuality"/>, the read behind it. The database is shared with
/// every other test, so each test puts its outfits in a window of its own far in the past and reads only that window.
/// </summary>
public sealed class AdminQualityTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A day in 2002 nobody else writes to; each test takes its own.</summary>
    private static DateTimeOffset Window(int day) => new DateTimeOffset(2002, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(day * 10);

    private sealed record Seed(
        OutfitStatus Status,
        OutfitKind Kind = OutfitKind.Daily,
        Occasion? Occasion = null,
        string Rule = "tonal",
        DismissReason? Reason = null,
        OutfitSlot[]? Slots = null,
        OutfitCompleteness Completeness = OutfitCompleteness.Complete,
        int DayOffset = 0);

    /// <summary>Inserts the outfits for <paramref name="userId"/> and backdates them to <paramref name="at"/> (plus their day offset).</summary>
    private async Task<List<Guid>> SeedAsync(TestUser user, DateTimeOffset at, params Seed[] seeds)
    {
        // Every piece must point at a wardrobe item or a product; one catalog product stands in for all of them.
        var productId = await user.Client.SeedProductAsync("ust-giyim/gomlek", "White", 3, cancellationToken: Ct);
        return await factory.WithDbAsync(async db =>
    {
        var outfits = seeds.Select(seed =>
        {
            var outfit = new Outfit
            {
                UserId = user.Id,
                Kind = seed.Kind,
                Completeness = seed.Completeness,
                Occasion = seed.Occasion,
                ForDate = DateOnly.FromDateTime(at.UtcDateTime).AddDays(seed.DayOffset),
                Rule = seed.Rule,
                Hash = Guid.NewGuid().ToString("N"),
                Status = seed.Status,
                DismissReason = seed.Reason,
                Narration = "-",
                NarrationInput = "{}",
            };
            outfit.Items.AddRange((seed.Slots ?? [OutfitSlot.Top, OutfitSlot.Bottom, OutfitSlot.Shoes])
                .Select((slot, position) => new OutfitItem { Slot = slot, Role = OutfitItemRole.Gap, ProductId = productId, Position = position }));
            return (outfit, seed);
        }).ToList();

        db.Outfits.AddRange(outfits.Select(pair => pair.outfit));
        await db.SaveChangesAsync(Ct);

        foreach (var (outfit, seed) in outfits)
        {
            var createdAt = at.AddDays(seed.DayOffset);
            await db.Outfits.Where(row => row.Id == outfit.Id).ExecuteUpdateAsync(setters => setters.SetProperty(row => row.CreatedAt, createdAt), Ct);
        }

        return outfits.Select(pair => pair.outfit.Id).ToList();
    });
    }

    private Task<SuggestionQualityReport> ReadAsync(DateTimeOffset from, DateTimeOffset to) =>
        factory.WithDbAsync(db => SuggestionQuality.ReadAsync(db, from, to, Ct));

    [Fact]
    public async Task AnEmptyWindowHasZeroCountsAndNoRates()
    {
        var from = Window(0);

        var report = await ReadAsync(from, from.AddDays(1));

        Assert.Equal(0, report.Total.Suggested);
        Assert.Null(report.Total.LikeRate);
        Assert.Null(report.Total.LikedOfSuggested);
        Assert.Null(report.Total.DismissedOfSuggested);
        Assert.Equal(0, report.Users);
        Assert.Empty(report.ByKind);
        Assert.Empty(report.BySlot);
        Assert.Empty(report.DismissReasons);
        Assert.Empty(report.Days);
    }

    [Fact]
    public async Task EachOutfitCountsOnceByWhereItEndedUp()
    {
        var from = Window(1);
        using var user = await factory.CreateUserAsync(Ct);
        using var other = await factory.CreateUserAsync(Ct);
        var ids = await SeedAsync(user, from.AddHours(1),
            new Seed(OutfitStatus.Saved),
            new Seed(OutfitStatus.Worn),
            new Seed(OutfitStatus.Dismissed, Reason: DismissReason.TooFormal),
            new Seed(OutfitStatus.Suggested));
        await SeedAsync(other, from.AddHours(2), new Seed(OutfitStatus.Dismissed, Reason: DismissReason.TooFormal));

        // The worn outfit was saved first, then worn: two feedback rows, one outfit. Feedback is counted by when it was given.
        await factory.WithDbAsync(async db =>
        {
            db.OutfitFeedback.AddRange(
                new OutfitFeedback { OutfitId = ids[1], UserId = user.Id, Action = OutfitFeedbackAction.Save, CreatedAt = from.AddHours(3) },
                new OutfitFeedback { OutfitId = ids[1], UserId = user.Id, Action = OutfitFeedbackAction.Worn, CreatedAt = from.AddHours(4) },
                new OutfitFeedback { OutfitId = ids[3], UserId = user.Id, Action = OutfitFeedbackAction.Shuffle, CreatedAt = from.AddHours(5) },
                new OutfitFeedback { OutfitId = ids[3], UserId = user.Id, Action = OutfitFeedbackAction.Shuffle, CreatedAt = from.AddDays(2) });
            return await db.SaveChangesAsync(Ct);
        });

        var report = await ReadAsync(from, from.AddDays(1));

        Assert.Equal(5, report.Total.Suggested);
        Assert.Equal(2, report.Total.Liked);
        Assert.Equal(2, report.Total.Dismissed);
        Assert.Equal(4, report.Total.Answered);
        Assert.Equal(0.5, report.Total.LikeRate);
        Assert.Equal(0.4, report.Total.LikedOfSuggested);
        Assert.Equal(2, report.Users);
        Assert.Equal(1, report.Saves);
        Assert.Equal(1, report.Wears);
        Assert.Equal(1, report.Shuffles);
        Assert.Equal(0, report.Dismisses);
        var reason = Assert.Single(report.DismissReasons);
        Assert.Equal(("TooFormal", 2, 1.0), (reason.Reason, reason.Count, reason.Share));
    }

    [Fact]
    public async Task TheBreakdownsSplitByKindOccasionRuleSlotAndCompleteness()
    {
        var from = Window(2);
        using var user = await factory.CreateUserAsync(Ct);
        await SeedAsync(user, from.AddHours(1),
            new Seed(OutfitStatus.Saved, OutfitKind.Daily, Occasion.Office, "tonal"),
            new Seed(OutfitStatus.Dismissed, OutfitKind.Daily, Occasion.Office, "contrast", DismissReason.DontLikeColors,
                [OutfitSlot.Top, OutfitSlot.Bottom, OutfitSlot.Accessory, OutfitSlot.Accessory]),
            new Seed(OutfitStatus.Dismissed, OutfitKind.HowToWear, null, "contrast", DismissReason.WrongWeather, Completeness: OutfitCompleteness.NeedsOne),
            new Seed(OutfitStatus.Suggested, OutfitKind.HowToWear, Occasion.Weekend, "tonal", DayOffset: 1));

        var report = await ReadAsync(from, from.AddDays(3));

        Assert.Equal(4, report.Total.Suggested);

        var daily = report.ByKind.Single(row => row.Label == "Daily");
        Assert.Equal((2, 1, 1), (daily.Suggested, daily.Liked, daily.Dismissed));
        var howToWear = report.ByKind.Single(row => row.Label == "HowToWear");
        Assert.Equal((2, 0, 1), (howToWear.Suggested, howToWear.Liked, howToWear.Dismissed));
        Assert.Equal(0.0, howToWear.LikeRate);

        Assert.Equal((2, 1, 1), Counts(report.ByOccasion, "Office"));
        Assert.Equal((1, 0, 1), Counts(report.ByOccasion, SuggestionQuality.None));
        Assert.Equal((1, 0, 0), Counts(report.ByOccasion, "Weekend"));
        Assert.Null(report.ByOccasion.Single(row => row.Label == "Weekend").LikeRate);

        Assert.Equal((2, 1, 0), Counts(report.ByRule, "tonal"));
        Assert.Equal((2, 0, 2), Counts(report.ByRule, "contrast"));

        // Two accessories are one accessory outfit; every outfit has a top.
        Assert.Equal((4, 1, 2), Counts(report.BySlot, "Top"));
        Assert.Equal((1, 0, 1), Counts(report.BySlot, "Accessory"));
        Assert.Equal((3, 1, 1), Counts(report.BySlot, "Shoes"));

        Assert.Equal((3, 1, 1), Counts(report.ByCompleteness, "Complete"));
        Assert.Equal((1, 0, 1), Counts(report.ByCompleteness, "NeedsOne"));

        Assert.Equal(["DontLikeColors", "WrongWeather"], report.DismissReasons.Select(reason => reason.Reason));
        Assert.All(report.DismissReasons, reason => Assert.Equal(0.5, reason.Share));

        var day = DateOnly.FromDateTime(from.UtcDateTime);
        Assert.Equal(
            [new SuggestionQualityDay(day, 3, 1, 2), new SuggestionQualityDay(day.AddDays(1), 1, 0, 0)],
            report.Days);

        // Most suggested first, then by label (Bottom and Top tie at four).
        Assert.Equal(["Bottom", "Top", "Shoes", "Accessory"], report.BySlot.Select(row => row.Label));
    }

    [Fact]
    public async Task OnlyOutfitsCreatedInTheWindowCount()
    {
        var from = Window(3);
        using var user = await factory.CreateUserAsync(Ct);
        await SeedAsync(user, from.AddMinutes(-1), new Seed(OutfitStatus.Saved));
        await SeedAsync(user, from, new Seed(OutfitStatus.Dismissed));
        await SeedAsync(user, from.AddDays(1), new Seed(OutfitStatus.Saved));

        var report = await ReadAsync(from, from.AddDays(1));

        Assert.Equal((1, 0, 1), (report.Total.Suggested, report.Total.Liked, report.Total.Dismissed));
        Assert.Empty(report.DismissReasons);
    }

    [Fact]
    public async Task ThePageShowsTheReportBehindTheAdminSignIn()
    {
        using var anonymous = factory.CreateClient(new() { AllowAutoRedirect = false });
        using var unsigned = await anonymous.GetAsync("/admin/quality", Ct);
        Assert.Equal(HttpStatusCode.Redirect, unsigned.StatusCode);

        using var user = await factory.CreateUserAsync(Ct);
        await factory.WithDbAsync(async db =>
        {
            var outfit = new Outfit
            {
                UserId = user.Id,
                Kind = OutfitKind.Daily,
                Completeness = OutfitCompleteness.Complete,
                ForDate = new DateOnly(2040, 1, 1),
                Rule = "admin-quality-page-rule",
                Hash = Guid.NewGuid().ToString("N"),
                Status = OutfitStatus.Dismissed,
                DismissReason = DismissReason.TooCasual,
                Narration = "-",
                NarrationInput = "{}",
            };
            db.Outfits.Add(outfit);
            return await db.SaveChangesAsync(Ct);
        });
        using var browser = await factory.SignedInAdminAsync(Ct);

        var html = await browser.GetHtmlAsync("/admin/quality?days=7", Ct);

        Assert.Contains("<h1>Suggestion quality</h1>", html, StringComparison.Ordinal);
        Assert.Contains("<strong>Last 7 days</strong>", html, StringComparison.Ordinal);
        Assert.Contains("admin-quality-page-rule", html, StringComparison.Ordinal);
        Assert.Contains("TooCasual", html, StringComparison.Ordinal);
        Assert.Contains("Why users dismiss", html, StringComparison.Ordinal);
        Assert.DoesNotContain(user.Id.ToString(), html, StringComparison.OrdinalIgnoreCase);

        // An unknown window falls back to 30 days.
        var fallback = await browser.GetHtmlAsync("/admin/quality?days=5", Ct);
        Assert.Contains("<strong>Last 30 days</strong>", fallback, StringComparison.Ordinal);
    }

    private static (int Suggested, int Liked, int Dismissed) Counts(IReadOnlyList<SuggestionQualityRow> rows, string label)
    {
        var row = rows.Single(candidate => candidate.Label == label);
        return (row.Suggested, row.Liked, row.Dismissed);
    }
}
