using System.Globalization;
using Kombin.Api.Common.Time;
using Kombin.Api.Persistence;
using Microsoft.AspNetCore.Mvc;

namespace Kombin.Api.Features.Admin.Pages;

/// <summary>
/// "Öneri kalitesi" (<c>/admin/quality</c>): how the outfit engine's suggestions fare with users, read from the outfits and their feedback by
/// <see cref="SuggestionQuality"/>. Read-only and aggregate: no user, outfit or piece is named on the page.
/// </summary>
internal sealed class QualityModel(KombinDbContext db, IClock clock) : AdminPageModel
{
    /// <summary>The windows the page offers, in days; anything else falls back to the default.</summary>
    public static readonly IReadOnlyList<int> Windows = [7, 30, 90];

    public const int DefaultDays = 30;

    [BindProperty(SupportsGet = true)]
    public int Days { get; set; } = DefaultDays;

    public SuggestionQualityReport Report { get; private set; } = null!;

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        if (!Windows.Contains(Days))
        {
            Days = DefaultDays;
        }

        var now = clock.UtcNow;
        Report = await SuggestionQuality.ReadAsync(db, now.AddDays(-Days), now, cancellationToken);
    }

    /// <summary><c>42%</c>, or a dash when there is nothing to divide by.</summary>
    public static string Percent(double? rate) =>
        rate is { } value ? value.ToString("P0", CultureInfo.InvariantCulture).Replace(" ", string.Empty, StringComparison.Ordinal) : "-";
}
