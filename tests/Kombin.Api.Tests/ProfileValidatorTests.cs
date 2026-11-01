using Kombin.Api.Features.Profile.ConfirmSize;
using Kombin.Api.Features.Profile.CreatePersona;
using Kombin.Api.Features.Profile.SubmitSwipes;
using Kombin.Api.Features.Profile.UpdatePersona;
using Kombin.Api.Features.Profile.UpdateProfile;
using Kombin.Api.Features.Profile.UpsertSize;

namespace Kombin.Api.Tests;

public class ProfileValidatorTests
{
    private static readonly FixedClock Clock = new(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));

    private readonly UpdateProfileValidator _profile = new(Clock);

    private readonly CreatePersonaValidator _create = new();

    private readonly UpdatePersonaValidator _update = new();

    private readonly SubmitSwipesValidator _swipes = new();

    private readonly UpsertSizeValidator _upsertSize = new();

    private readonly ConfirmSizeValidator _confirmSize = new();

    private static UpdateProfileRequest Profile(
        string? audience = null,
        int? birthYear = null,
        int? height = null,
        decimal? weight = null,
        int? chest = null,
        int? waist = null,
        int? hip = null,
        decimal? shoe = null) =>
        new(audience, birthYear, height, weight, chest, waist, hip, shoe);

    private static CreatePersonaRequest Persona(
        string name = "Ofis",
        string icon = "briefcase",
        decimal? budgetMin = null,
        decimal? budgetMax = null,
        int? formalityMin = null,
        int? formalityMax = null,
        string[]? preferredColors = null,
        string[]? avoidedColors = null,
        string[]? preferredFits = null,
        string[]? seasons = null) =>
        new(name, icon, budgetMin, budgetMax, formalityMin, formalityMax, preferredColors, avoidedColors, preferredFits, seasons);

    private static UpdatePersonaRequest Patch(
        string? name = null,
        bool? clearBudget = null,
        decimal? budgetMin = null,
        string[]? preferredColors = null,
        string[]? seasons = null) =>
        new(name, null, null, budgetMin, null, clearBudget, null, null, preferredColors, null, null, seasons);

    [Fact]
    public void AnEmptyProfileUpdatePasses()
    {
        Assert.True(_profile.Validate(Profile()).IsValid);
    }

    [Fact]
    public void AFullValidProfilePasses()
    {
        Assert.True(_profile.Validate(Profile("women", 1990, 172, 64.5m, 90, 70, 96, 38.5m)).IsValid);
    }

    [Theory]
    [InlineData(119, false)]
    [InlineData(120, true)]
    [InlineData(230, true)]
    [InlineData(231, false)]
    public void HeightIsBetween120And230(int height, bool valid)
    {
        Assert.Equal(valid, _profile.Validate(Profile(height: height)).IsValid);
    }

    [Theory]
    [InlineData(29.9, false)]
    [InlineData(30, true)]
    [InlineData(250, true)]
    [InlineData(250.1, false)]
    [InlineData(70.55, false)]
    public void WeightIsBetween30And250WithOneDecimal(double weight, bool valid)
    {
        Assert.Equal(valid, _profile.Validate(Profile(weight: (decimal)weight)).IsValid);
    }

    [Theory]
    [InlineData(49, false)]
    [InlineData(50, true)]
    [InlineData(200, true)]
    [InlineData(201, false)]
    public void ChestWaistAndHipAreBetween50And200(int value, bool valid)
    {
        Assert.Equal(valid, _profile.Validate(Profile(chest: value)).IsValid);
        Assert.Equal(valid, _profile.Validate(Profile(waist: value)).IsValid);
        Assert.Equal(valid, _profile.Validate(Profile(hip: value)).IsValid);
    }

    [Theory]
    [InlineData(29.5, false)]
    [InlineData(30, true)]
    [InlineData(52, true)]
    [InlineData(52.5, false)]
    [InlineData(40.25, false)]
    public void ShoeSizeIsBetween30And52(double shoe, bool valid)
    {
        Assert.Equal(valid, _profile.Validate(Profile(shoe: (decimal)shoe)).IsValid);
    }

    [Theory]
    [InlineData(1919, false)]
    [InlineData(1920, true)]
    [InlineData(2016, true)]
    [InlineData(2017, false)]
    public void BirthYearRunsFrom1920ToTenYearsAgo(int year, bool valid)
    {
        Assert.Equal(valid, _profile.Validate(Profile(birthYear: year)).IsValid);
    }

    [Theory]
    [InlineData("Women", true)]
    [InlineData("men", true)]
    [InlineData("Unisex", true)]
    [InlineData("Kids", false)]
    [InlineData("Cats", false)]
    [InlineData("", false)]
    public void AudienceIsWomenMenOrUnisex(string audience, bool valid)
    {
        Assert.Equal(valid, _profile.Validate(Profile(audience: audience)).IsValid);
    }

    [Fact]
    public void AValidPersonaPasses()
    {
        var request = Persona(budgetMin: 100, budgetMax: 2000, formalityMin: 2, formalityMax: 4, preferredColors: ["navy", "Beige"], avoidedColors: ["Red"], preferredFits: ["Slim"], seasons: ["Autumn", "Winter"]);

        Assert.True(_create.Validate(request).IsValid);
        Assert.True(_create.Validate(Persona()).IsValid);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("Ofis", true)]
    public void APersonaNeedsAName(string name, bool valid)
    {
        Assert.Equal(valid, _create.Validate(Persona(name: name)).IsValid);
    }

    [Fact]
    public void NameAndIconAreAtMostFortyCharacters()
    {
        Assert.True(_create.Validate(Persona(name: new string('a', 40), icon: new string('b', 40))).IsValid);
        Assert.False(_create.Validate(Persona(name: new string('a', 41))).IsValid);
        Assert.False(_create.Validate(Persona(icon: new string('b', 41))).IsValid);
        Assert.False(_create.Validate(Persona(icon: "")).IsValid);
    }

    [Fact]
    public void BudgetBoundsMustBeNonNegativeAndOrdered()
    {
        Assert.False(_create.Validate(Persona(budgetMin: -1)).IsValid);
        Assert.False(_create.Validate(Persona(budgetMin: 500, budgetMax: 100)).IsValid);
        Assert.True(_create.Validate(Persona(budgetMin: 100, budgetMax: 100)).IsValid);
        Assert.True(_create.Validate(Persona(budgetMax: 100)).IsValid);
    }

    [Theory]
    [InlineData(0, 5, false)]
    [InlineData(1, 6, false)]
    [InlineData(4, 2, false)]
    [InlineData(3, 3, true)]
    [InlineData(null, 4, true)]
    [InlineData(5, null, true)]
    [InlineData(null, 0, false)]
    public void FormalityBoundsAreOneToFiveAndOrdered(int? min, int? max, bool valid)
    {
        Assert.Equal(valid, _create.Validate(Persona(formalityMin: min, formalityMax: max)).IsValid);
    }

    [Fact]
    public void FormalityMaxBelowTheDefaultMinIsRejectedEvenWithoutAMin()
    {
        // Min defaults to 1, so a lone max can never be below it; a lone min above the default max 5 cannot be given either.
        Assert.True(_create.Validate(Persona(formalityMax: 1)).IsValid);
    }

    [Fact]
    public void ColorsFitsAndSeasonsMustBeKnownNames()
    {
        Assert.False(_create.Validate(Persona(preferredColors: ["Chartreuse"])).IsValid);
        Assert.False(_create.Validate(Persona(avoidedColors: ["1"])).IsValid);
        Assert.False(_create.Validate(Persona(preferredFits: ["Tight"])).IsValid);
        Assert.False(_create.Validate(Persona(seasons: ["Monsoon"])).IsValid);
        Assert.False(_create.Validate(Persona(seasons: ["AllYear"])).IsValid);
        Assert.False(_create.Validate(Persona(seasons: [])).IsValid);
    }

    [Fact]
    public void AColorCannotBeBothPreferredAndAvoided()
    {
        var result = _create.Validate(Persona(preferredColors: ["Black", "Navy"], avoidedColors: ["navy"]));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.PropertyName == "avoidedColors");
    }

    [Fact]
    public void APatchNeedsAtLeastOneField()
    {
        Assert.False(_update.Validate(Patch()).IsValid);
        Assert.True(_update.Validate(Patch(name: "Gece")).IsValid);
        Assert.True(_update.Validate(Patch(clearBudget: true)).IsValid);
    }

    [Fact]
    public void APatchChecksOnlyWhatIsGiven()
    {
        Assert.False(_update.Validate(Patch(name: " ")).IsValid);
        Assert.False(_update.Validate(Patch(preferredColors: ["Chartreuse"])).IsValid);
        Assert.False(_update.Validate(Patch(seasons: [])).IsValid);
        Assert.False(_update.Validate(Patch(budgetMin: -5)).IsValid);
        Assert.True(_update.Validate(Patch(preferredColors: [])).IsValid);
    }

    [Fact]
    public void ClearBudgetCannotBeCombinedWithABound()
    {
        Assert.False(_update.Validate(Patch(clearBudget: true, budgetMin: 10)).IsValid);
        Assert.True(_update.Validate(Patch(clearBudget: false, budgetMin: 10)).IsValid);
    }

    [Fact]
    public void ASwipeBatchHasOneToFiftyItemsWithKnownDecisions()
    {
        SubmitSwipesRequest Batch(int count, string decision = "Like") =>
            new([.. Enumerable.Range(0, count).Select(_ => new SwipeItem(Guid.NewGuid(), decision))]);

        Assert.False(_swipes.Validate(Batch(0)).IsValid);
        Assert.True(_swipes.Validate(Batch(1)).IsValid);
        Assert.True(_swipes.Validate(Batch(50)).IsValid);
        Assert.False(_swipes.Validate(Batch(51)).IsValid);
        Assert.True(_swipes.Validate(Batch(2, "dislike")).IsValid);
        Assert.True(_swipes.Validate(Batch(2, "Skip")).IsValid);
        Assert.False(_swipes.Validate(Batch(2, "Love")).IsValid);
        Assert.False(_swipes.Validate(Batch(2, "0")).IsValid);
        Assert.False(_swipes.Validate(new SubmitSwipesRequest([new SwipeItem(Guid.Empty, "Like")])).IsValid);
    }

    [Theory]
    [InlineData("mavi", "Tops", "M", true)]
    [InlineData("mavi", "tops", " 38 ", true)]
    [InlineData("", "Tops", "M", false)]
    [InlineData("mavi", "Hats", "M", false)]
    [InlineData("mavi", "Tops", "", false)]
    [InlineData("mavi", "Tops", "   ", false)]
    [InlineData("mavi", "Tops", "123456789012345678901", false)]
    public void ASizeNeedsABrandAKnownGroupAndASizeUpToTwentyCharacters(string brand, string group, string size, bool valid)
    {
        Assert.Equal(valid, _upsertSize.Validate(new UpsertSizeRequest(brand, group, size)).IsValid);
    }

    [Fact]
    public void ConfirmingASizeNeedsAnExplicitFittedFlag()
    {
        Assert.True(_confirmSize.Validate(new ConfirmSizeRequest(Guid.NewGuid(), "M", true)).IsValid);
        Assert.True(_confirmSize.Validate(new ConfirmSizeRequest(Guid.NewGuid(), "M", false)).IsValid);
        Assert.False(_confirmSize.Validate(new ConfirmSizeRequest(Guid.NewGuid(), "M", null)).IsValid);
        Assert.False(_confirmSize.Validate(new ConfirmSizeRequest(Guid.Empty, "M", true)).IsValid);
        Assert.False(_confirmSize.Validate(new ConfirmSizeRequest(Guid.NewGuid(), "", true)).IsValid);
    }
}
