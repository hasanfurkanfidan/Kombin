namespace Kombin.Api.Persistence.Entities.Recommendation;

/// <summary>
/// A saved packing plan ("Valiz hazırlama"). <see cref="RequestJson"/> is what the user asked for (city, dates, plans per day, options) and
/// <see cref="PlanJson"/> the plan the server computed (<c>TripPlanSnapshot</c>: wardrobe item IDs per day, scores, the weather used, gap offers);
/// names and images are read live when it is shown. Re-planning overwrites the plan. Destination and dates are personal data: deleted with the account.
/// </summary>
internal sealed class Trip : BaseEntity
{
    public const int MaxNameLength = 60;

    public const int MaxCityLength = 80;

    public Guid UserId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string City { get; set; } = string.Empty;

    public DateOnly StartDate { get; set; }

    public DateOnly EndDate { get; set; }

    public string RequestJson { get; set; } = "{}";

    public string PlanJson { get; set; } = "{}";

    /// <summary>The size of the packing list, for the list screen.</summary>
    public int PieceCount { get; set; }

    public int CoveredDays { get; set; }

    public int TotalDays { get; set; }

    public DateTimeOffset PlannedAt { get; set; }
}
