using Microsoft.Extensions.Options;

namespace Kombin.Api.Features.Wardrobe.BulkAdd;

/// <summary>
/// <c>BulkAdd:*</c>, "Toplu ekle" (design: <c>docs/design/bulk-add.md</c>). Every number of the feature is here: a new number is a setting, never a
/// constant. The defaults are the owner's answers to the design note's open questions 2 and 3.
/// </summary>
internal sealed class BulkAddOptions
{
    public const string SectionName = "BulkAdd";

    /// <summary>Off by default (the brief's stop rule is provisional): every route answers 404 <c>Wardrobe.BulkDisabled</c> and <c>bulk-add-analyze</c> does nothing.</summary>
    public bool Enabled { get; set; }

    /// <summary>Pieces kept from one photo, in reading order; the service sends at most 12 and the rest are cut off (and counted, so the app can say so).</summary>
    public int MaxPiecesPerPhoto { get; set; } = 12;

    /// <summary>Counted <c>process</c> calls per user and Istanbul day, refusals included (a failed analysis of ours or vision's is not counted).</summary>
    public int MaxPhotosPerDay { get; set; } = 8;

    /// <summary>Batches a user may have open (not yet confirmed, cancelled or expired): bounds the staging storage and the jobs one user can queue.</summary>
    public int MaxOpenBatches { get; set; } = 3;

    /// <summary>An unfinished batch and its staged files are deleted this long after the batch was created.</summary>
    public TimeSpan StagingRetention { get; set; } = TimeSpan.FromHours(24);

    /// <summary>An ended batch stays as a tombstone (counts and enums, no image key, no piece) this long: the daily limit and the counters need it.</summary>
    public TimeSpan TombstoneRetention { get; set; } = TimeSpan.FromDays(7);

    /// <summary>A piece whose embedding is at least this similar to one of the user's own items carries the "Dolabında benzeri var." hint; calibrated on the owner's photos.</summary>
    public double DuplicateSimilarity { get; set; } = 0.92;

    /// <summary>When vision answers busy the job waits <c>Retry-After</c> clamped to [<see cref="BusyRetryMin"/>, <see cref="BusyRetryMax"/>] and tries once more.</summary>
    public TimeSpan BusyRetryMin { get; set; } = TimeSpan.FromSeconds(3);

    public TimeSpan BusyRetryMax { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>The timeout of one <c>/v1/garment/bulk</c> call (the service takes ~6 s a photo at most, and queues for up to 20 s).</summary>
    public TimeSpan VisionTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>A <c>process</c> call this soon after the last one does not enqueue the analysis again.</summary>
    public TimeSpan ReprocessAfter { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>The cleanup marks a batch still <c>Processing</c> after this <c>Failed</c> (<c>Interrupted</c>): the job that held it died.</summary>
    public TimeSpan InterruptedAfter { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>The presigned links of the staged images in the status answer.</summary>
    public TimeSpan StagedUrlLifetime { get; set; } = TimeSpan.FromHours(1);
}

/// <summary>Refuses a start with impossible <c>BulkAdd:*</c> values, listing every one.</summary>
internal sealed class BulkAddOptionsValidator : IValidateOptions<BulkAddOptions>
{
    /// <summary>The most pieces the vision service ever returns (its brief); a larger setting could never be reached.</summary>
    public const int VisionPieceLimit = 12;

    public static IEnumerable<string> Problems(BulkAddOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var problems = new List<string>();
        void Require(bool ok, string problem)
        {
            if (!ok)
            {
                problems.Add(problem);
            }
        }

        Require(options.MaxPiecesPerPhoto is >= 1 and <= VisionPieceLimit, $"BulkAdd:MaxPiecesPerPhoto must be between 1 and {VisionPieceLimit} (the vision service's limit).");
        Require(options.MaxPhotosPerDay >= 1, "BulkAdd:MaxPhotosPerDay must be at least 1.");
        Require(options.MaxOpenBatches >= 1, "BulkAdd:MaxOpenBatches must be at least 1.");
        Require(options.StagingRetention > TimeSpan.Zero, "BulkAdd:StagingRetention must be positive.");
        Require(options.TombstoneRetention >= TimeSpan.FromDays(1), "BulkAdd:TombstoneRetention must be at least one day (the daily limit counts the day's batches).");
        Require(options.DuplicateSimilarity is > 0 and <= 1, "BulkAdd:DuplicateSimilarity must be above 0 and at most 1.");
        Require(options.BusyRetryMin >= TimeSpan.Zero, "BulkAdd:BusyRetryMin must not be negative.");
        Require(options.BusyRetryMax >= options.BusyRetryMin, "BulkAdd:BusyRetryMax must be at least BulkAdd:BusyRetryMin.");
        Require(options.VisionTimeout > TimeSpan.Zero && options.VisionTimeout <= TimeSpan.FromMinutes(5), "BulkAdd:VisionTimeout must be positive and at most 5 minutes.");
        Require(options.ReprocessAfter > TimeSpan.Zero, "BulkAdd:ReprocessAfter must be positive.");
        Require(options.InterruptedAfter > options.VisionTimeout, "BulkAdd:InterruptedAfter must be longer than BulkAdd:VisionTimeout (a running job is not interrupted).");
        Require(options.StagedUrlLifetime > TimeSpan.Zero, "BulkAdd:StagedUrlLifetime must be positive.");

        return problems;
    }

    public ValidateOptionsResult Validate(string? name, BulkAddOptions options)
    {
        var problems = Problems(options).ToList();

        return problems.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(problems);
    }
}
