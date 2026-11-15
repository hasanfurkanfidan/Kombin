using Kombin.Api.Features.Profile;
using Kombin.Api.Features.Profile.AnswerSizePrompt;
using Kombin.Api.Persistence.Entities.Profile;

namespace Kombin.Api.Tests;

public class SizePromptRulesTests
{
    private static readonly DateTimeOffset Clicked = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AQuestionOpensAfterThreeDaysAndExpiresAfterThirty()
    {
        Assert.Equal(TimeSpan.FromDays(3), SizePromptRules.OpensAfter);
        Assert.Equal(Clicked.AddDays(30), SizePromptRules.ExpiresAt(Clicked));
        Assert.False(SizePromptRules.IsExpired(Clicked, Clicked.AddDays(30).AddTicks(-1)));
        Assert.True(SizePromptRules.IsExpired(Clicked, Clicked.AddDays(30)));
    }

    [Fact]
    public void OnlyAccessoriesHaveNoSize()
    {
        Assert.All([SizeGroup.Tops, SizeGroup.Bottoms, SizeGroup.Outerwear, SizeGroup.Shoes], group => Assert.True(SizePromptRules.HasSize(group)));
        Assert.False(SizePromptRules.HasSize(SizeGroup.Accessories));
    }

    [Theory]
    [InlineData("BoughtFits", "M", null, "Confirm")]
    [InlineData("BoughtFits", "L", "M", "Confirm")]
    [InlineData("BoughtTooSmall", null, "M", "Doubt")]
    [InlineData("BoughtTooLarge", "M", "M", "Doubt")]
    [InlineData("BoughtTooSmall", "L", "M", "None")]
    [InlineData("BoughtTooSmall", "M", null, "None")]
    [InlineData("BoughtTooLarge", null, null, "None")]
    [InlineData("NotBought", "M", "M", "None")]
    [InlineData("NeverAsk", null, "M", "None")]
    public void OnlyAConfirmedFitSetsASizeAndTooSmallOrLargeOnlyDoubtsTheStoredOne(string answer, string? answered, string? stored, string expected)
    {
        Assert.Equal(Enum.Parse<SizeEffect>(expected), SizePromptRules.EffectOf(Enum.Parse<SizePromptAnswer>(answer), answered, stored));
    }

    [Fact]
    public void TheValidatorNeedsAKnownAnswerAndASizeForAFit()
    {
        var validator = new AnswerSizePromptValidator();

        Assert.True(validator.Validate(new AnswerSizePromptRequest("BoughtFits", "M")).IsValid);
        Assert.True(validator.Validate(new AnswerSizePromptRequest("boughttoosmall", null)).IsValid);
        Assert.True(validator.Validate(new AnswerSizePromptRequest("BoughtTooLarge", "L")).IsValid);
        Assert.True(validator.Validate(new AnswerSizePromptRequest("NotBought", null)).IsValid);
        Assert.True(validator.Validate(new AnswerSizePromptRequest("NeverAsk", "")).IsValid);

        Assert.False(validator.Validate(new AnswerSizePromptRequest("BoughtFits", null)).IsValid);
        Assert.False(validator.Validate(new AnswerSizePromptRequest("BoughtFits", "  ")).IsValid);
        Assert.False(validator.Validate(new AnswerSizePromptRequest("Maybe", "M")).IsValid);
        Assert.False(validator.Validate(new AnswerSizePromptRequest("", null)).IsValid);
        Assert.False(validator.Validate(new AnswerSizePromptRequest("BoughtTooLarge", new string('X', 21))).IsValid);
    }
}
