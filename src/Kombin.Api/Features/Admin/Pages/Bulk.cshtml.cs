using Kombin.Api.Common.Time;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Wardrobe;

namespace Kombin.Api.Features.Admin.Pages;

/// <summary>The last days of "Toplu ekle" as counts: the sums of the confirmed batches' counters, and how many batches were refused for each reason.</summary>
internal sealed record BulkNumbers(
    int Batches,
    int Confirmed,
    int PiecesShown,
    int PreTicked,
    int Ticked,
    int UntickedPreTicks,
    int TickedExtras,
    decimal EditsPerTickedPiece,
    double? MedianSecondsToReady);

/// <summary>
/// The stop rule's numbers (<c>/admin/bulk</c>, design note <c>docs/design/bulk-add.md</c>, section f) for the last <see cref="Days"/> days, read from the batches' own
/// counters: counts only, no photo, no piece and no user. Read-only (<c>OnGet</c> only); the sign-in is the admin folder's.
/// </summary>
internal sealed class BulkModel(KombinDbContext db, IClock clock) : AdminPageModel
{
    public const int Days = 7;

    public BulkNumbers Numbers { get; private set; } = new(0, 0, 0, 0, 0, 0, 0, 0, null);

    public IReadOnlyList<KeyValuePair<string, int>> Refusals { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        (Numbers, Refusals) = await ReadAsync(db, now.AddDays(-Days), now.AddMinutes(1), cancellationToken);
    }

    /// <summary>The numbers of the batches created in [<paramref name="since"/>, <paramref name="until"/>), the page's one query (a seam for the tests).</summary>
    internal static async Task<(BulkNumbers Numbers, IReadOnlyList<KeyValuePair<string, int>> Refusals)> ReadAsync(
        KombinDbContext db,
        DateTimeOffset since,
        DateTimeOffset until,
        CancellationToken cancellationToken)
    {
        var window = db.BulkBatches.AsNoTracking().Where(batch => batch.CreatedAt >= since && batch.CreatedAt < until);

        var confirmed = await window
            .Where(batch => batch.Status == BulkBatchStatus.Confirmed)
            .GroupBy(_ => 1)
            .Select(group => new
            {
                Batches = group.Count(),
                Shown = group.Sum(batch => batch.PiecesShown),
                PreTicked = group.Sum(batch => batch.PiecesPreTicked),
                Ticked = group.Sum(batch => batch.PiecesTicked),
                TickedPreTicked = group.Sum(batch => batch.TickedPreTicked),
                Edits = group.Sum(batch => batch.CategoryEdits + batch.ColourEdits),
            })
            .SingleOrDefaultAsync(cancellationToken);

        var processed = await window.CountAsync(batch => batch.Counted || batch.Status == BulkBatchStatus.Refused, cancellationToken);
        var seconds = await window
            .Where(batch => batch.VisionMs != null && batch.Counted && batch.Status != BulkBatchStatus.Refused)
            .Select(batch => batch.VisionMs!.Value)
            .ToListAsync(cancellationToken);

        IReadOnlyList<KeyValuePair<string, int>> refusals =
        [
            .. (await window
                .Where(batch => batch.Status == BulkBatchStatus.Refused && batch.Refusal != null)
                .GroupBy(batch => batch.Refusal!)
                .Select(group => new { Code = group.Key, Count = group.Count() })
                .ToListAsync(cancellationToken))
                .OrderByDescending(row => row.Count)
                .ThenBy(row => row.Code, StringComparer.Ordinal)
                .Select(row => KeyValuePair.Create(row.Code, row.Count)),
        ];

        var numbers = new BulkNumbers(
            processed,
            confirmed?.Batches ?? 0,
            confirmed?.Shown ?? 0,
            confirmed?.PreTicked ?? 0,
            confirmed?.Ticked ?? 0,
            (confirmed?.PreTicked ?? 0) - (confirmed?.TickedPreTicked ?? 0),
            (confirmed?.Ticked ?? 0) - (confirmed?.TickedPreTicked ?? 0),
            confirmed is { Ticked: > 0 } ? Math.Round((decimal)confirmed.Edits / confirmed.Ticked, 2) : 0,
            Median(seconds));

        return (numbers, refusals);
    }

    private static double? Median(List<int> milliseconds)
    {
        if (milliseconds.Count == 0)
        {
            return null;
        }

        milliseconds.Sort();
        var middle = milliseconds.Count / 2;
        var value = milliseconds.Count % 2 == 1 ? milliseconds[middle] : (milliseconds[middle - 1] + milliseconds[middle]) / 2.0;

        return Math.Round(value / 1000.0, 1);
    }
}
