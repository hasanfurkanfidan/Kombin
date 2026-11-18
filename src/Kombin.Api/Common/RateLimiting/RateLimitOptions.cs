using Microsoft.Extensions.Options;

namespace Kombin.Api.Common.RateLimiting;

/// <summary>One limit: at most <see cref="PermitLimit"/> requests per <see cref="WindowSeconds"/> (a fixed window) for one caller.</summary>
internal sealed class RateLimitWindow
{
    public RateLimitWindow()
    {
    }

    public RateLimitWindow(int permitLimit, int windowSeconds)
    {
        PermitLimit = permitLimit;
        WindowSeconds = windowSeconds;
    }

    public int PermitLimit { get; set; }

    public int WindowSeconds { get; set; }

    public TimeSpan Window => TimeSpan.FromSeconds(WindowSeconds);
}

/// <summary>
/// An endpoint with two limits: the anonymous caller's (per IP) and the signed-in user's (per user). Visual search and budget outfits (both an
/// acquisition screen and an expensive query) have one each.
/// </summary>
internal sealed class TwoTierRateLimits
{
    public RateLimitWindow Anonymous { get; set; } = new(20, 3600);

    public RateLimitWindow Authenticated { get; set; } = new(60, 3600);
}

/// <summary>
/// <c>RateLimits:*</c>. The defaults are the launch limits (documented in CLAUDE.md, "Rate limiting"). They live in memory and per instance:
/// a second API instance would double them. <c>Enabled=false</c> turns every policy and the global fallback off (the test host does, except
/// in the dedicated rate-limit tests).
/// </summary>
internal sealed class RateLimitOptions
{
    public const string SectionName = "RateLimits";

    public bool Enabled { get; set; } = true;

    /// <summary>Login, register, external sign-in, refresh, cancel-deletion and <c>DELETE /me</c>: 10 per 5 minutes per IP.</summary>
    public RateLimitWindow Auth { get; set; } = new(10, 300);

    public TwoTierRateLimits Lens { get; set; } = new();

    /// <summary>Budget outfits, signed in or not: like visual search, 20 per hour per IP anonymously, 60 per hour per user signed in (own buckets).</summary>
    public TwoTierRateLimits Budget { get; set; } = new();

    /// <summary>Trip planning (plan, save, re-plan: one bucket): 30 per hour per user.</summary>
    public RateLimitWindow Trips { get; set; } = new(30, 3600);

    /// <summary>Kombin Ligi's next pair, vote and report (one bucket): 120 per 5 minutes per user. Only stops a client in a loop; 30 pairs a day is the real cap.</summary>
    public RateLimitWindow League { get; set; } = new(120, 300);

    /// <summary>"Manken üstünde gör": drawing (also the cheap <c>check=true</c>) and "Yanlış görünüyor" reports (one bucket): 120 per 5 minutes per user. New drawings have their own limit (<c>TryOn:RendersPerHour</c>).</summary>
    public RateLimitWindow TryOn { get; set; } = new(120, 300);

    /// <summary>Upload URLs of the wardrobe and of posts: 60 per hour per user.</summary>
    public RateLimitWindow Uploads { get; set; } = new(60, 3600);

    /// <summary>The analytics intake: 600 per 5 minutes per user (per IP while anonymous).</summary>
    public RateLimitWindow Events { get; set; } = new(600, 300);

    /// <summary>The affiliate redirect: 120 per minute per IP.</summary>
    public RateLimitWindow Go { get; set; } = new(120, 60);

    /// <summary>Everything else, and on top of the policies above: 600 per minute per IP.</summary>
    public RateLimitWindow Global { get; set; } = new(600, 60);
}

internal sealed class RateLimitOptionsValidator : IValidateOptions<RateLimitOptions>
{
    public ValidateOptionsResult Validate(string? name, RateLimitOptions options)
    {
        var failures = new List<string>();

        Check("Auth", options.Auth, failures);
        Check("Lens:Anonymous", options.Lens.Anonymous, failures);
        Check("Lens:Authenticated", options.Lens.Authenticated, failures);
        Check("Budget:Anonymous", options.Budget.Anonymous, failures);
        Check("Budget:Authenticated", options.Budget.Authenticated, failures);
        Check("Trips", options.Trips, failures);
        Check("League", options.League, failures);
        Check("TryOn", options.TryOn, failures);
        Check("Uploads", options.Uploads, failures);
        Check("Events", options.Events, failures);
        Check("Go", options.Go, failures);
        Check("Global", options.Global, failures);

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void Check(string name, RateLimitWindow window, List<string> failures)
    {
        if (window.PermitLimit < 1)
        {
            failures.Add($"RateLimits:{name}:PermitLimit must be at least 1.");
        }

        if (window.WindowSeconds < 1)
        {
            failures.Add($"RateLimits:{name}:WindowSeconds must be at least 1.");
        }
    }
}
