using Microsoft.Extensions.Options;

namespace Kombin.Api.Features.Social;

/// <summary>
/// <c>Creators:*</c> (env <c>Creators__SharePercent</c>, ...). <c>SharePercent</c> is the creator's share of the commission and is snapshotted
/// on every referral event. The other three only feed the estimate on <c>GET /me/creator/stats</c> until real conversions are imported.
/// </summary>
internal sealed class CreatorOptions
{
    public const string SectionName = "Creators";

    public decimal SharePercent { get; set; } = 40m;

    /// <summary>Average basket in TRY (placeholder until conversions arrive).</summary>
    public decimal AverageOrderValue { get; set; } = 750m;

    /// <summary>Affiliate commission on an order, percent (typical Turkish fashion programmes pay 5-10).</summary>
    public decimal CommissionPercent { get; set; } = 8m;

    /// <summary>Share of buy clicks that end in an order, percent. 100 reproduces the brief's formula, which counts every click as a sale.</summary>
    public decimal ConversionRatePercent { get; set; } = 3m;
}

internal sealed class CreatorOptionsValidator : IValidateOptions<CreatorOptions>
{
    public ValidateOptionsResult Validate(string? name, CreatorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        static bool IsPercent(decimal value) => value is >= 0 and <= 100;

        return IsPercent(options.SharePercent) && IsPercent(options.CommissionPercent) && IsPercent(options.ConversionRatePercent) && options.AverageOrderValue >= 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail("Creators: SharePercent, CommissionPercent and ConversionRatePercent must be 0-100, AverageOrderValue at least 0.");
    }
}

/// <summary>An estimate of what a creator earned from clicks, with every input shown so the app can say how it was computed.</summary>
internal sealed record EarningsEstimate(
    decimal Amount,
    string Currency,
    bool IsEstimate,
    int Clicks,
    decimal ConversionRatePercent,
    decimal AverageOrderValue,
    decimal CommissionPercent,
    decimal SharePercent);

/// <summary>Pure. <c>amount = clicks × conversion rate × average order value × commission × share</c>, rounded to kuruş.</summary>
internal static class CreatorEarnings
{
    public static EarningsEstimate Estimate(int clicks, CreatorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var amount = Math.Max(0, clicks)
            * (options.ConversionRatePercent / 100m)
            * options.AverageOrderValue
            * (options.CommissionPercent / 100m)
            * (options.SharePercent / 100m);

        return new EarningsEstimate(
            Math.Round(amount, 2, MidpointRounding.AwayFromZero),
            "TRY",
            true,
            Math.Max(0, clicks),
            options.ConversionRatePercent,
            options.AverageOrderValue,
            options.CommissionPercent,
            options.SharePercent);
    }

    /// <summary>The creator's part of a reported commission (for the future conversion import).</summary>
    public static decimal CreatorShare(decimal commission, decimal sharePercent) =>
        Math.Round(commission * sharePercent / 100m, 2, MidpointRounding.AwayFromZero);
}
