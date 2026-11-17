using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.Profile;

namespace Kombin.Api.Features.TryOn.Rules;

/// <summary>
/// The body the mannequin is proportioned from: <see cref="UserProfile"/>'s audience and measurements (there is no shoulder measurement).
/// Pure. Never logged and never sent to analytics: only the render request carries it.
/// </summary>
internal sealed record TryOnBody(Audience Audience, int? HeightCm, decimal? WeightKg, int? ChestCm, int? WaistCm, int? HipCm)
{
    /// <summary>No profile yet: every value is the audience default (unisex).</summary>
    public static TryOnBody From(UserProfile? profile) => profile is null
        ? new TryOnBody(Audience.Unisex, null, null, null, null, null)
        : new TryOnBody(profile.Audience, profile.HeightCm, profile.WeightKg, profile.ChestCm, profile.WaistCm, profile.HipCm);

    /// <summary>The service's audience: <c>women</c>, <c>men</c>, else <c>unisex</c>.</summary>
    public string ServiceAudience => Audience switch
    {
        Audience.Women => "women",
        Audience.Men => "men",
        _ => "unisex",
    };

    /// <summary>The service's default height for the audience (<c>mannequin.py</c>: women 165, men 177, unisex their mean).</summary>
    public double DefaultHeightCm => Audience switch
    {
        Audience.Women => 165,
        Audience.Men => 177,
        _ => 171,
    };

    /// <summary>The service's default BMI for the audience (women 22, men 24, unisex 23): a body without a weight has it.</summary>
    public double DefaultBmi => Audience switch
    {
        Audience.Women => 22,
        Audience.Men => 24,
        _ => 23,
    };

    /// <summary>
    /// The BMI the service resolves: the weight over the height squared, with the audience's default height when there is none; without a weight,
    /// the audience's default BMI (inside the range).
    /// </summary>
    public double Bmi
    {
        get
        {
            if (WeightKg is not { } weight || weight <= 0)
            {
                return DefaultBmi;
            }

            var metres = (HeightCm is > 0 ? HeightCm.Value : DefaultHeightCm) / 100.0;

            return (double)weight / (metres * metres);
        }
    }

    /// <summary>SHA-256 (hex) of the values: a render made for another hash shows other proportions and is deleted.</summary>
    public string Hash() => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical())));

    /// <summary>The values as one invariant string, the body part of the cache key.</summary>
    public string Canonical() => string.Join(
        '|',
        ServiceAudience,
        HeightCm?.ToString(CultureInfo.InvariantCulture) ?? "-",
        WeightKg?.ToString(CultureInfo.InvariantCulture) ?? "-",
        ChestCm?.ToString(CultureInfo.InvariantCulture) ?? "-",
        WaistCm?.ToString(CultureInfo.InvariantCulture) ?? "-",
        HipCm?.ToString(CultureInfo.InvariantCulture) ?? "-");
}
