using System.Globalization;
using Kombin.Api.Persistence.Entities.Engagement;

namespace Kombin.Api.Features.Notification;

internal readonly record struct NotificationText(string Title, string Body);

/// <summary>What an alert notification talks about; names are as stored in the catalog.</summary>
internal sealed record AlertCopyInput(
    string CategoryName,
    string BrandName,
    string ProductName,
    decimal? PriceBefore,
    decimal? PriceAfter,
    IReadOnlyList<string> Sizes);

/// <summary>
/// All user-facing notification text, in Turkish, in one place. Money is written the Turkish way ("2.149 ₺", "849,90 ₺") by hand
/// instead of through a culture, because the runtime image may run with invariant globalization and carry no tr-TR data.
/// </summary>
internal static class Copy
{
    public const string Lira = "₺";

    public const int MaxTitleLength = 120;

    public const int MaxBodyLength = 300;

    public static string Money(decimal amount)
    {
        var rounded = Math.Round(amount, 2, MidpointRounding.AwayFromZero);
        var magnitude = Math.Abs(rounded);
        var whole = decimal.Truncate(magnitude);
        var cents = (int)((magnitude - whole) * 100m);

        var digits = whole.ToString("N0", CultureInfo.InvariantCulture).Replace(',', '.');
        var number = cents == 0 ? digits : $"{digits},{cents:00}";

        return $"{(rounded < 0 ? "-" : string.Empty)}{number} {Lira}";
    }

    /// <summary>Lower-casing with the Turkish dotted/dotless i rules ("BLAZER" stays "blazer", "ISPARTA" becomes "ısparta").</summary>
    public static string TurkishLower(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return string.Create(value.Length, value, static (span, source) =>
        {
            for (var index = 0; index < source.Length; index++)
            {
                span[index] = source[index] switch
                {
                    'I' => 'ı',
                    'İ' => 'i',
                    var other => char.ToLowerInvariant(other),
                };
            }
        });
    }

    /// <summary>"Mavi" + "Yün Karışımlı Blazer" is "Mavi Yün Karışımlı Blazer"; a product name that already starts with its brand is not repeated.</summary>
    public static string ProductDisplayName(string brandName, string productName) =>
        brandName.Length == 0 || productName.StartsWith(brandName, StringComparison.OrdinalIgnoreCase)
            ? productName
            : $"{brandName} {productName}";

    public static NotificationText ForAlert(AlertKind kind, AlertCopyInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var text = kind switch
        {
            AlertKind.PriceDrop => PriceDrop(input),
            AlertKind.BackInStock => BackInStock(input),
            _ => AnyChange(input),
        };

        return new NotificationText(Clip(text.Title, MaxTitleLength), Clip(text.Body, MaxBodyLength));
    }

    public static NotificationText ForNewMatches(string personaName, int count, int lowestScore, IEnumerable<string> productNames) =>
        new(
            Clip($"\"{personaName}\" tarzına %{lowestScore / 10 * 10} üzeri uyumlu {count} yeni parça", MaxTitleLength),
            Clip(string.Join(", ", productNames), MaxBodyLength));

    public static string Clip(string text, int maxLength) =>
        text.Length <= maxLength ? text : string.Concat(text.AsSpan(0, maxLength - 1), "…");

    private static NotificationText PriceDrop(AlertCopyInput input)
    {
        var name = ProductDisplayName(input.BrandName, input.ProductName);
        var category = TurkishLower(input.CategoryName);

        var title = input is { PriceBefore: { } before, PriceAfter: { } after } && before > after
            ? $"Takip ettiğin {category} {Money(before - after)} düştü"
            : $"Takip ettiğin {category} hedef fiyatına indi";

        var body = input.PriceAfter is { } price
            ? $"{name} artık {Money(price)} — hedef fiyatının altında."
            : $"{name} hedef fiyatının altında.";

        return new NotificationText(title, body);
    }

    private static NotificationText BackInStock(AlertCopyInput input)
    {
        var name = ProductDisplayName(input.BrandName, input.ProductName);
        var sizes = input.Sizes.Count > 0 ? $" — {string.Join(", ", input.Sizes)} bedenleri mevcut" : string.Empty;
        var price = input.PriceAfter is { } after ? $", fiyatı {Money(after)}." : ".";

        return new NotificationText(
            $"Takip ettiğin {TurkishLower(input.CategoryName)} tekrar stokta",
            $"{name} yeniden satışta{sizes}{price}");
    }

    private static NotificationText AnyChange(AlertCopyInput input)
    {
        var name = ProductDisplayName(input.BrandName, input.ProductName);
        var category = TurkishLower(input.CategoryName);

        if (input is { PriceBefore: { } before, PriceAfter: { } after } && before != after)
        {
            var previous = $"(önceki fiyat {Money(before)})";

            return after < before
                ? new NotificationText($"Takip ettiğin {category} {Money(before - after)} düştü", $"{name} artık {Money(after)} {previous}.")
                : new NotificationText($"Takip ettiğin {category} {Money(after - before)} zamlandı", $"{name} şimdi {Money(after)} {previous}.");
        }

        return new NotificationText($"Takip ettiğin {category} için stok durumu değişti", $"{name} için stok bilgisi güncellendi.");
    }
}
