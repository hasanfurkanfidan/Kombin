using Kombin.Api.Persistence.Entities.Catalog;

namespace Kombin.Api.Persistence.Entities.Profile;

/// <summary>One of a user's styles ("Ofis", "Hafta sonu", ...). Archived personas are hidden but keep their swipes.</summary>
internal sealed class Persona : BaseEntity
{
    public const int MaxActivePerUser = 6;

    public Guid UserId { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>A key the app maps to an icon; free text.</summary>
    public string Icon { get; set; } = string.Empty;

    public int SortOrder { get; set; }

    public bool IsArchived { get; set; }

    public decimal? BudgetMin { get; set; }

    public decimal? BudgetMax { get; set; }

    /// <summary>1 = sportswear, 5 = formal, like <see cref="Product.Formality"/>.</summary>
    public short FormalityMin { get; set; } = 1;

    public short FormalityMax { get; set; } = 5;

    /// <summary><see cref="ProductColor"/> names.</summary>
    public string[] PreferredColors { get; set; } = [];

    public string[] AvoidedColors { get; set; } = [];

    /// <summary><see cref="ProductFit"/> names.</summary>
    public string[] PreferredFits { get; set; } = [];

    public Season Seasons { get; set; } = Season.AllYear;
}
