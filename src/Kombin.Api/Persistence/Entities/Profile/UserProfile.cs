using Kombin.Api.Persistence.Entities.Catalog;

namespace Kombin.Api.Persistence.Entities.Profile;

/// <summary>Per-user shopping preferences and body measurements. Not a <see cref="BaseEntity"/>: the user ID is the identity. Created lazily.</summary>
internal sealed class UserProfile : IAuditable
{
    public Guid UserId { get; set; }

    /// <summary>What the user shops for: Women, Men or Unisex (never Kids).</summary>
    public Audience Audience { get; set; } = Audience.Unisex;

    public int? BirthYear { get; set; }

    public int? HeightCm { get; set; }

    public decimal? WeightKg { get; set; }

    public int? ChestCm { get; set; }

    public int? WaistCm { get; set; }

    public int? HipCm { get; set; }

    public decimal? ShoeSizeEu { get; set; }

    public Guid? ActivePersonaId { get; set; }

    /// <summary>Set once, when a persona first reaches the like threshold in the swipe onboarding.</summary>
    public DateTimeOffset? OnboardingCompletedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
