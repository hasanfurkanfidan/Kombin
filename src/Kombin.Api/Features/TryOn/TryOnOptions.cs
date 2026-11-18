using Kombin.Api.Common.Hosting;
using Kombin.Api.Common.Results;
using Microsoft.Extensions.Options;

namespace Kombin.Api.Features.TryOn;

/// <summary><c>TryOn:*</c>, "Manken üstünde gör" (<c>docs/design/try-on.md</c>). Every number of the feature is here.</summary>
internal sealed class TryOnOptions : IFeatureOptions
{
    public const string SectionName = "TryOn";

    /// <summary>Off by default: the routes answer 404 <c>TryOn.Disabled</c> and <c>tryon-prepare</c> does nothing.</summary>
    public bool Enabled { get; set; }

    static Error IFeatureOptions.Disabled => TryOnErrors.Disabled;

    /// <summary>Above this BMI the outfit's edges run off the picture (the service's <c>MAX_BMI</c>): the action is hidden.</summary>
    public double MaxBmi { get; set; } = 36;

    /// <summary>New renders per user per rolling hour (cache hits are free).</summary>
    public int RendersPerHour { get; set; } = 30;

    /// <summary>The image is this wide and twice as tall (the service accepts 300-900).</summary>
    public int RenderWidth { get; set; } = 600;

    public TimeSpan RenderUrlLifetime { get; set; } = TimeSpan.FromHours(1);

    /// <summary>A render nobody asked for this long is deleted (row and image).</summary>
    public TimeSpan RenderRetention { get; set; } = TimeSpan.FromDays(30);

    /// <summary>A transient prepare failure is tried once more after this.</summary>
    public TimeSpan RetryAfter { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>After the second transient failure (or a refused re-prepare of a garment that has an answer) the next try waits this long.</summary>
    public TimeSpan FailedRetryAfter { get; set; } = TimeSpan.FromHours(24);

    /// <summary>How long a prepare holds its garment row; nobody else prepares it meanwhile.</summary>
    public TimeSpan Lease { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>A request that finds a piece unprepared enqueues a prepare for it at most this often.</summary>
    public TimeSpan RequestInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Wardrobe items prepared per run of <c>tryon-prepare</c>.</summary>
    public int WardrobeBatch { get; set; } = 20;

    /// <summary>Catalogue products prepared per run during the day, one at a time (interactive calls keep the vision service's slots).</summary>
    public int DayCatalogueBatch { get; set; } = 5;

    /// <summary>Catalogue products prepared per run at night (<see cref="NightStartsAt"/> to <see cref="NightEndsAt"/>, Istanbul).</summary>
    public int NightCatalogueBatch { get; set; } = 40;

    public TimeOnly NightStartsAt { get; set; } = new(1, 0);

    public TimeOnly NightEndsAt { get; set; } = new(7, 0);

    /// <summary>A run stops starting new prepares after this, so minute runs do not pile up.</summary>
    public TimeSpan RunBudget { get; set; } = TimeSpan.FromSeconds(50);

    /// <summary>The largest retailer photo downloaded (Jack &amp; Jones' transparent packshots reach 45 MB); it is scaled before sending.</summary>
    public long MaxSourceImageBytes { get; set; } = 50L * 1024 * 1024;

    /// <summary>A photo with more pixels than this is not decoded at all.</summary>
    public long MaxSourcePixels { get; set; } = 60_000_000;
}

/// <summary>Refuses a start with impossible <c>TryOn:*</c> values, listing every one.</summary>
internal sealed class TryOnOptionsValidator : IValidateOptions<TryOnOptions>
{
    public ValidateOptionsResult Validate(string? name, TryOnOptions options)
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

        Require(options.MaxBmi > 0, "TryOn:MaxBmi must be positive.");
        Require(options.RendersPerHour >= 1, "TryOn:RendersPerHour must be at least 1.");
        Require(options.RenderWidth is >= 300 and <= 900, "TryOn:RenderWidth must be between 300 and 900 (the renderer's range).");
        Require(options.RenderUrlLifetime > TimeSpan.Zero, "TryOn:RenderUrlLifetime must be positive.");
        Require(options.RenderRetention > TimeSpan.Zero, "TryOn:RenderRetention must be positive.");
        Require(options.RetryAfter > TimeSpan.Zero, "TryOn:RetryAfter must be positive.");
        Require(options.FailedRetryAfter >= options.RetryAfter, "TryOn:FailedRetryAfter must be at least TryOn:RetryAfter.");
        Require(options.Lease > TimeSpan.Zero, "TryOn:Lease must be positive.");
        Require(options.RequestInterval >= TimeSpan.Zero, "TryOn:RequestInterval must not be negative.");
        Require(options.WardrobeBatch >= 1, "TryOn:WardrobeBatch must be at least 1.");
        Require(options.DayCatalogueBatch >= 0, "TryOn:DayCatalogueBatch must not be negative.");
        Require(options.NightCatalogueBatch >= 0, "TryOn:NightCatalogueBatch must not be negative.");
        Require(options.RunBudget > TimeSpan.Zero, "TryOn:RunBudget must be positive.");
        Require(options.MaxSourceImageBytes > 0, "TryOn:MaxSourceImageBytes must be positive.");
        Require(options.MaxSourcePixels > 0, "TryOn:MaxSourcePixels must be positive.");

        return problems.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(problems);
    }
}
